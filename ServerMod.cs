﻿using Harmony;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace ServerMod
{
    [StaticConstructorOnStartup]
    public class ServerMod
    {
        public const int DEFAULT_PORT = 30502;

        public static String username;
        public static Server server;
        public static Connection client;
        public static Connection localServerConnection;

        public static byte[] savedWorld;
        public static bool saving = false;
        public static CountdownLock worldDownloading = new CountdownLock();

        public static Queue<ScheduledServerAction> actions = new Queue<ScheduledServerAction>();

        static ServerMod()
        {
            GenCommandLine.TryGetCommandLineArg("username", out username);
            if (username == null)
                username = SteamUtility.SteamPersonaName;
            if (username == "???")
                username = "Player" + Rand.Range(0, 9999);

            var gameobject = new GameObject();
            gameobject.AddComponent<OnMainThread>();
            UnityEngine.Object.DontDestroyOnLoad(gameobject);

            var harmony = HarmonyInstance.Create("servermod");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public static class Packets
    {
        public const int CLIENT_REQUEST_WORLD = 0;
        public const int CLIENT_WORLD_FINISHED = 1;
        public const int CLIENT_ACTION_REQUEST = 2;
        public const int CLIENT_USERNAME = 3;

        public const int SERVER_WORLD_DATA = 0;
        public const int SERVER_ACTION_SCHEDULE = 1;
        public const int SERVER_PAUSE_FOR_WORLD_DOWNLOAD = 2;
    }

    public enum ServerAction : int
    {
        PAUSE, UNPAUSE
    }

    public struct ScheduledServerAction
    {
        public readonly int ticks;
        public readonly ServerAction action;

        public ScheduledServerAction(int ticks, ServerAction action)
        {
            this.ticks = ticks;
            this.action = action;
        }
    }

    public static class Extensions
    {
        public static byte[] Append(this byte[] arr1, byte[] arr2)
        {
            byte[] result = new byte[arr1.Length + arr2.Length];
            arr1.CopyTo(result, 0);
            arr2.CopyTo(result, arr1.Length);
            return result;
        }
    }

    public class LocalClientConnection : Connection
    {
        public LocalServerConnection server;

        public LocalClientConnection() : base(null)
        {
        }

        public override void Send(int id, byte[] message = null)
        {
            message = message ?? new byte[] { 0 };
            server.State?.Message(id, message);
        }

        public override void Close()
        {
            connectionClosed();
            server.connectionClosed();
        }

        public override string ToString()
        {
            return "Local";
        }
    }

    public class LocalServerConnection : Connection
    {
        public LocalClientConnection client;

        public LocalServerConnection() : base(null)
        {
        }

        public override void Send(int id, byte[] message = null)
        {
            message = message ?? new byte[] { 0 };
            client.State?.Message(id, message);
        }

        public override void Close()
        {
            connectionClosed();
            client.connectionClosed();
        }

        public override string ToString()
        {
            return "Local";
        }
    }

    public class CountdownLock
    {
        public AutoResetEvent eventObj = new AutoResetEvent(false);
        private HashSet<object> ids = new HashSet<object>();

        public void Add(object id)
        {
            lock (ids)
            {
                ids.Add(id);
            }
        }

        public void Wait()
        {
            eventObj.WaitOne();
        }

        public bool Done(object id)
        {
            lock (ids)
            {
                if (!ids.Remove(id))
                    return false;

                if (ids.Count == 0)
                {
                    eventObj.Set();
                    return true;
                }

                return false;
            }
        }
    }

    public class ServerWorldState : ConnectionState
    {
        private Guid guid = System.Guid.NewGuid();
        private string username;

        public ServerWorldState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.CLIENT_REQUEST_WORLD)
            {
                OnMainThread.Queue(() =>
                {
                    if (ServerMod.savedWorld == null)
                    {
                        if (!ServerMod.saving)
                            SaveWorld();

                        LongEventHandler.QueueLongEvent(() =>
                        {
                            Connection.Send(Packets.SERVER_WORLD_DATA, ServerMod.savedWorld);
                            ServerMod.worldDownloading.Add(guid);
                        }, "Sending the world", true, null);
                    }
                    else
                    {
                        Connection.Send(Packets.SERVER_WORLD_DATA, ServerMod.savedWorld);
                        ServerMod.worldDownloading.Add(guid);
                    }
                });
            }
            else if (id == Packets.CLIENT_WORLD_FINISHED)
            {
                OnMainThread.Queue(() =>
                {
                    if (ServerMod.worldDownloading.Done(guid))
                    {
                        ServerMod.savedWorld = null;
                        Log.Message("world download finished");
                    }

                    this.Connection.State = new ServerPlayingState(this.Connection);
                });
            }
            else if (id == Packets.CLIENT_USERNAME)
            {
                this.username = Encoding.ASCII.GetString(data);
            }
        }

        private void SaveWorld()
        {
            ServerMod.server.SendToAll(Packets.SERVER_PAUSE_FOR_WORLD_DOWNLOAD, null, this.Connection, ServerMod.localServerConnection);
            ServerMod.saving = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            LongEventHandler.QueueLongEvent(() =>
            {
                using (var stream = new MemoryStream())
                {
                    // prepare
                    Scribe.mode = LoadSaveMode.Saving;

                    XmlWriterSettings xmlWriterSettings = new XmlWriterSettings();
                    xmlWriterSettings.Indent = true;
                    xmlWriterSettings.IndentChars = "\t";
                    XmlWriter xmlWriter = XmlWriter.Create(stream, xmlWriterSettings);
                    typeof(ScribeSaver).GetField("writer", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Scribe.saver, xmlWriter);
                    xmlWriter.WriteStartDocument();
                    Scribe.EnterNode("savegame");

                    // save
                    ScribeMetaHeaderUtility.WriteMetaHeader();
                    Scribe.EnterNode("game");
                    sbyte visibleMapIndex = -1;
                    Scribe_Values.Look<sbyte>(ref visibleMapIndex, "visibleMapIndex", -1, false);
                    typeof(Game).GetMethod("ExposeSmallComponents", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Current.Game, null);
                    World world = Current.Game.World;
                    Scribe_Deep.Look<World>(ref world, "world");
                    List<Map> maps = new List<Map>();
                    Scribe_Collections.Look<Map>(ref maps, "maps", LookMode.Deep);
                    Scribe.ExitNode();

                    // finalize
                    Scribe.ExitNode();
                    xmlWriter.WriteEndDocument();
                    xmlWriter.Flush();
                    xmlWriter.Close();

                    Scribe.mode = LoadSaveMode.Inactive;

                    ServerMod.savedWorld = stream.ToArray();
                    ServerMod.saving = false;

                    LongEventHandler.QueueLongEvent(() =>
                    {
                        ServerMod.worldDownloading.Wait();
                    }, "Sending the world", true, null);
                }
            }, "Saving world for incoming players", false, null);
        }

        public override void Disconnect()
        {
        }
    }

    public class ServerPlayingState : ConnectionState
    {
        public ServerPlayingState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.CLIENT_ACTION_REQUEST)
            {
                OnMainThread.Queue(() =>
                {
                    ServerAction action = (ServerAction)BitConverter.ToInt32(data, 0);
                    ScheduledServerAction schdl = new ScheduledServerAction(Find.TickManager.TicksGame + 15, action);

                    byte[] send = BitConverter.GetBytes((int)schdl.ticks).Append(BitConverter.GetBytes((int)action));
                    ServerMod.server.SendToAll(Packets.SERVER_ACTION_SCHEDULE, send);

                    Log.Message("server got request from client at " + Find.TickManager.TicksGame + " for " + action + " " + schdl.ticks);
                });
            }
        }

        public override void Disconnect()
        {
        }
    }

    public class ClientWorldState : ConnectionState
    {
        public ClientWorldState(Connection connection) : base(connection)
        {
            connection.Send(Packets.CLIENT_USERNAME, Encoding.ASCII.GetBytes(ServerMod.username));
            connection.Send(Packets.CLIENT_REQUEST_WORLD);
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.SERVER_WORLD_DATA)
            {
                OnMainThread.Queue(() =>
                {
                    ServerMod.savedWorld = data;
                    Log.Message("World size: " + data.Length);

                    LongEventHandler.QueueLongEvent(() =>
                    {
                        MemoryUtility.ClearAllMapsAndWorld();
                        Current.Game = new Game();
                        Current.Game.InitData = new GameInitData();
                        Current.Game.InitData.gameToLoad = "server";
                        ServerMod.client.State = new ClientPlayingState(ServerMod.client);
                    }, "Play", "LoadingLongEvent", true, null);
                });
            }
        }

        public override void Disconnect()
        {
        }
    }

    public class ClientPlayingState : ConnectionState
    {
        public ClientPlayingState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.SERVER_ACTION_SCHEDULE)
            {
                OnMainThread.Queue(() =>
                {
                    ScheduledServerAction schdl = new ScheduledServerAction(BitConverter.ToInt32(data, 0), (ServerAction)BitConverter.ToInt32(data, 4));
                    ServerMod.actions.Enqueue(schdl);
                    Log.Message("client got request from server at " + Find.TickManager.TicksGame + " for action " + schdl.action + " " + schdl.ticks);
                });
            }
        }

        public override void Disconnect()
        {
        }
    }

    public class OnMainThread : MonoBehaviour
    {
        private static readonly Queue<Action> queue = new Queue<Action>();

        public void Update()
        {
            lock (queue)
                while (queue.Count > 0)
                    queue.Dequeue().Invoke();

            if (Current.Game != null)
                // when paused, execute immediately
                while (ServerMod.actions.Count > 0 && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
                    ExecuteServerAction(ServerMod.actions.Dequeue());
        }

        public static void Queue(Action action)
        {
            lock (queue)
                queue.Enqueue(action);
        }

        public static void ExecuteServerAction(ScheduledServerAction action)
        {
            if (action.action == ServerAction.PAUSE)
                TickUpdatePatch.SetSpeed(TimeSpeed.Paused);
            else if (action.action == ServerAction.UNPAUSE)
                TickUpdatePatch.SetSpeed(TimeSpeed.Normal);

            Log.Message("executed a scheduled action " + action.action);
        }
    }

    public class PlayerFactions : WorldComponent
    {
        public Dictionary<String, Faction> playerFactions = new Dictionary<string, Faction>();

        public PlayerFactions(World world) : base(world) { }

        public override void ExposeData()
        {
        }

    }

}

