using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using ProtoBuf;
using Invalid.ModularEncountersSystems.API;
using System.Text;
using VRage.Game.GUI.TextPanel;
using InvalidWave.Draygo.API;
using static VRageRender.MyBillboard;
using System.Xml;
using VRage.Game.ModAPI.Ingame.Utilities;
using System.Linq;

namespace Invalid.StarCoreMESAI.Data.Scripts.MESAPISpawning
{
    [ProtoInclude(1000, typeof(ChatCommandPacket))]
    [ProtoInclude(1001, typeof(CounterUpdatePacket))]
    [ProtoContract]
    public class Packet
    {
        public Packet()
        {
        }
    }

    [ProtoContract]
    public class ChatCommandPacket : Packet
    {
        [ProtoMember(1)]
        public string CommandString;

        public ChatCommandPacket()
        {
        }

        public ChatCommandPacket(string CommandString)
        {
            this.CommandString = CommandString;
        }
    }

    [ProtoContract]
    public class CounterUpdatePacket : Packet
    {
        [ProtoMember(1)]
        public int CounterValue;

        public CounterUpdatePacket()
        {
        }

        public CounterUpdatePacket(int counterValue)
        {
            this.CounterValue = counterValue;
        }
    }

    public class SpawnGroupInfo
    {
        public int Quantity { get; set; }
        public int SpawnTime { get; set; }

        public SpawnGroupInfo(int quantity, int spawnTime)
        {
            Quantity = quantity;
            SpawnTime = spawnTime;
        }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class SCMESWaveSpawnerComponent : MySessionComponentBase
    {
        private ushort netID = 23489;
        private MESApi SpawnerAPI;
        private bool registered = false;
        private static int aiShipsDestroyed = 0;
        private bool isEventTriggered = false;
        private DateTime lastEventTriggerTime;
        private const double EventResetIntervalSeconds = 1; // Time in seconds to reset the event trigger
        private DateTime lastBroadcastTime;

        // TextHUD API fields
        private HudAPIv2.HUDMessage aiShipsDestroyedHUD;
        private HudAPIv2 HudAPI;
        private bool hudInitialized = false;
        private HudAPIv2.HUDMessage waveTimerHUD;

        // Dictionary for spawn groups and their spawn times
        private Dictionary<string, SpawnGroupInfo> spawnGroupTimings = new Dictionary<string, SpawnGroupInfo>(); private DateTime lastWaveCheckTime;
        private bool wavesStarted = false; // Flag to control wave spawning
        private int additionalShipsPerWave = 0;

        public override void LoadData()
        {
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                SpawnerAPI = new MESApi();
            }

            // Initialize HudAPIv2 with a callback
            new HudAPIv2(OnHudApiReady);

            LoadWaveData();
            // Add other spawn groups and info as needed
        }

        private void LoadWaveData()
        {
            try
            {
                string fileName = "WaveData.cfg"; // Configuration file name

                if (MyAPIGateway.Utilities.FileExistsInLocalStorage(fileName, GetType()))
                {
                    using (var reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(fileName, GetType()))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // Trim whitespace from the line and split it by commas
                            string[] sections = line.Trim().Split(',');

                            if (sections.Length >= 3)
                            {
                                string name = sections[0].Trim();
                                int startTime;

                                if (int.TryParse(sections[1].Trim(), out startTime))
                                {
                                    List<string> prefabs = sections[2].Trim().Split(',').ToList();

                                    // Add the wave data to your dictionary or data structure
                                    spawnGroupTimings.Add(name, new SpawnGroupInfo(prefabs.Count, startTime));
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Handle the case where the configuration file doesn't exist
                    MyAPIGateway.Utilities.ShowMessage("Wave Data", "Configuration file not found.");
                }
            }
            catch (Exception e)
            {
                // Handle any exceptions that may occur during file reading or parsing
                MyAPIGateway.Utilities.ShowMessage("Wave Data", "Error loading configuration file: " + e.Message);
            }
        }



        private void OnHudApiReady()
        {
            // Initialize your HUD elements here
            aiShipsDestroyedHUD = new HudAPIv2.HUDMessage(
                new StringBuilder("AI Ships Destroyed: 0"),
                new Vector2D(0.5, 0.5), // Position on the screen
                Scale: 1.0,
                Blend: BlendTypeEnum.PostPP);

            waveTimerHUD = new HudAPIv2.HUDMessage(
                new StringBuilder("Next Wave: --:--"),
                new Vector2D(0.5, 0.55), // Adjust the position as needed
                Scale: 1.0,
                Blend: BlendTypeEnum.PostPP);

            hudInitialized = true;
        }

        public override void BeforeStart()
        {
            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered; // Listen for chat messages
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(netID, NetworkHandler);
        }

        public override void UpdateAfterSimulation()
        {
            if (MyAPIGateway.Multiplayer.IsServer && wavesStarted)
            {
                // Create a copy of the spawn group timings dictionary to avoid modification issues
                var spawnGroupTimingsCopy = new Dictionary<string, SpawnGroupInfo>(spawnGroupTimings);

                // Registering the event watcher only once
                if (SpawnerAPI.MESApiReady && !registered)
                {
                    SpawnerAPI.RegisterCompromisedRemoteWatcher(true, compromisedevent);
                    registered = true;
                }

                // Broadcasting counter update at regular intervals
                if ((DateTime.UtcNow - lastBroadcastTime).TotalSeconds >= 5)
                {
                    BroadcastCounter();
                    lastBroadcastTime = DateTime.UtcNow;
                }

                if (hudInitialized && spawnGroupTimingsCopy.Count > 0)
                {
                    var firstGroupInfo = new List<SpawnGroupInfo>(spawnGroupTimingsCopy.Values)[0];
                    var nextWaveSpawnTime = firstGroupInfo.SpawnTime;
                    var timeSinceStart = (int)(DateTime.UtcNow - lastWaveCheckTime).TotalSeconds;
                    var timeUntilNextWave = nextWaveSpawnTime - timeSinceStart;
                    if (timeUntilNextWave < 0) timeUntilNextWave = 0;
                    waveTimerHUD.Message.Clear().Append("Next Wave: " + (timeUntilNextWave / 60).ToString("D2") + ":" + (timeUntilNextWave % 60).ToString("D2"));
                }

                if ((DateTime.UtcNow - lastEventTriggerTime).TotalSeconds >= EventResetIntervalSeconds)
                {
                    if (isEventTriggered)
                    {
                        MyLog.Default.WriteLine("SCMESWaveSpawner: Resetting isEventTriggered flag.");
                    }
                    isEventTriggered = false;
                }

                foreach (var spawnGroup in spawnGroupTimingsCopy)
                {
                    var groupKey = spawnGroup.Key;
                    var groupValue = spawnGroup.Value;
                    var quantity = groupValue.Quantity + additionalShipsPerWave; // Add additional ships per wave, capped at 10
                    var spawnTime = groupValue.SpawnTime;

                    if ((DateTime.UtcNow - lastWaveCheckTime).TotalSeconds >= spawnTime)
                    {
                        Vector3D spawnCoords = new Vector3D(-20000, 0, 0);

                        for (int i = 0; i < quantity; i++)
                        {
                            // Spawn each unit separately
                            SpawnerAPI.SpawnSpaceCargoShip(spawnCoords, new List<string> { groupKey });
                        }

                        // Remove the spawned group from the dictionary to avoid respawning
                        spawnGroupTimings.Remove(groupKey);
                        break;
                    }
                }

                if (spawnGroupTimings.Count == 0)
                {
                    wavesStarted = false;
                }
            }
        }

        private void compromisedevent(IMyRemoteControl arg1, IMyCubeGrid arg2)
        {
            // Incrementing the counter each time an AI ship is destroyed
            aiShipsDestroyed++;
            MyLog.Default.WriteLine($"SCMESWaveSpawner: Compromised event triggered. AI Ships Destroyed: {aiShipsDestroyed}");

            // Updating the HUD element
            if (hudInitialized)
            {
                aiShipsDestroyedHUD.Message.Clear().Append($"AI Ships Destroyed: {aiShipsDestroyed}");
            }
        }


        private void BroadcastCounter()
        {
            var packet = new CounterUpdatePacket(aiShipsDestroyed);
            var serializedPacket = MyAPIGateway.Utilities.SerializeToBinary(packet);
            MyAPIGateway.Multiplayer.SendMessageToOthers(netID, serializedPacket);
        }

        private void NetworkHandler(ushort arg1, byte[] arg2, ulong arg3, bool arg4)
        {
            if (MyAPIGateway.Session.IsServer) return;

            var packet = MyAPIGateway.Utilities.SerializeFromBinary<Packet>(arg2);
            if (packet is CounterUpdatePacket)
            {
                var counterPacket = (CounterUpdatePacket)packet;
                // Update the local counter with the value from the server
                aiShipsDestroyed = counterPacket.CounterValue;
            }
        }

        private void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (messageText.StartsWith("/SCStartGauntlet", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the additional ship count from the command
                string[] commandParts = messageText.Split(' ');
                int parsedAdditionalShips; // Declare a separate variable
                if (commandParts.Length > 1 && int.TryParse(commandParts[1], out parsedAdditionalShips))
                {
                    // Cap the additional ships per wave to a maximum of 10
                    additionalShipsPerWave = Math.Min(parsedAdditionalShips, 10);

                    wavesStarted = true; // Start spawning waves
                    lastWaveCheckTime = DateTime.UtcNow; // Initialize the wave check time
                    sendToOthers = false;
                    MyAPIGateway.Utilities.ShowMessage("SCMESWaveSpawner", "Started spawning waves with additional ships per wave: " + additionalShipsPerWave);
                }
                else
                {
                    MyAPIGateway.Utilities.ShowMessage("SCMESWaveSpawner", "Invalid command format. Use /SCStartGauntlet X to specify additional ships per wave (max 10).");
                }
            }
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered; // Unsubscribe from chat message events
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(netID, NetworkHandler);
            SpawnerAPI.UnregisterListener();
            if (hudInitialized)
            {
                aiShipsDestroyedHUD.DeleteMessage();
            }
        }
    }
}
