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

namespace Invalid.spawnbattle
{
    [ProtoInclude(1000, typeof(PrefabSpawnPacket))]
    [ProtoContract]
    public class Packet
    {
        public Packet()
        {

        }
    }

    [ProtoContract]
    public class PrefabSpawnPacket : Packet
    {
        [ProtoMember(1)]
        public string PrefabName;

        [ProtoMember(2)]
        public int PrefabAmount;

        [ProtoMember(3)]  // New member for faction name
        public string FactionName;

        // Add a parameterless constructor required by ProtoBuf
        public PrefabSpawnPacket()
        {

        }

        public PrefabSpawnPacket(string prefabName, int prefabAmount, string factionName)
        {
            PrefabName = prefabName;
            PrefabAmount = prefabAmount;
            FactionName = factionName; // Set the faction name
        }
    }



    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class spawnbattleComponent : MySessionComponentBase
    {
        private Dictionary<string, string> prefabMap = new Dictionary<string, string>
        {
            { "LamiaAI", "LamiaAI" },

            // Add more prefab mappings here.
        };

        private int defaultSpawnCount = 1; // Default number of prefabs to spawn

        private ushort netID = 29396;

        private double minSpawnRadiusFromCenter = 1000; // Minimum spawn distance from the center in meters
        private double minSpawnRadiusFromGrids = 1000;  // Minimum spawn distance from other grids in meters
        private IMyFaction RedFaction = null;
        private IMyFaction BluFaction = null;

        public override void BeforeStart()
        {
            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered; // Listen for chat messages
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(netID, NetworkHandler);

            RedFaction = MyAPIGateway.Session.Factions.TryGetFactionByTag("RED");
            BluFaction = MyAPIGateway.Session.Factions.TryGetFactionByTag("BLU");
        }

        private void NetworkHandler(ushort arg1, byte[] arg2, ulong arg3, bool arg4)
        {
            if (!MyAPIGateway.Session.IsServer) return;

            Packet packet = MyAPIGateway.Utilities.SerializeFromBinary<Packet>(arg2);
            if (packet == null) return;

            PrefabSpawnPacket prefabPacket = packet as PrefabSpawnPacket;
            if (prefabPacket == null) return;

            if (prefabMap.ContainsKey(prefabPacket.PrefabName))
            {
                // Randomly choose the faction
                string factionName = MyUtils.GetRandomInt(0, 2) == 0 ? "RED" : "BLU";

                SpawnRandomPrefabs(prefabMap[prefabPacket.PrefabName], prefabPacket.PrefabAmount, factionName);
            }
            else
            {
                MyVisualScriptLogicProvider.SendChatMessage($"Prefab {prefabPacket.PrefabName} not found", "spawnbattle");
            }
        }


        private void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (!messageText.StartsWith("/spawnbattle", StringComparison.OrdinalIgnoreCase)) return;
            string[] parts = messageText.Split(' ');

            if (parts.Length == 1)
            {
                // Show list of available prefabs and usage instructions
                ShowPrefabList();
            }
            else if (parts.Length >= 2)
            {
                string prefabName = parts[1];
                int spawnCount = defaultSpawnCount;

                if (parts.Length >= 3)
                {
                    int parsedCount;
                    if (int.TryParse(parts[2], out parsedCount))
                    {
                        spawnCount = parsedCount;
                    }
                }

                // Randomly choose the faction
                string factionName = MyUtils.GetRandomInt(0, 2) == 0 ? "RED" : "BLU";

                // Create PrefabSpawnPacket instance with the factionName parameter
                PrefabSpawnPacket prefabSpawnPacket = new PrefabSpawnPacket(prefabName, spawnCount, factionName);

                // Serialize and send the packet
                byte[] data = MyAPIGateway.Utilities.SerializeToBinary(prefabSpawnPacket);
                MyAPIGateway.Multiplayer.SendMessageTo(netID, data, MyAPIGateway.Multiplayer.ServerId);

                MyAPIGateway.Utilities.ShowMessage("spawnbattle", $"Requesting: {prefabName} x {spawnCount}");
            }

            sendToOthers = false;
        }


        private void ShowPrefabList()
        {
            string prefabListMessage = "Available prefabs:";
            foreach (string prefabName in prefabMap.Keys)
            {
                prefabListMessage += "\n" + prefabName;
            }

            prefabListMessage += "\n\nTo spawn a prefab, type '/spawnbattle [prefabName] [amount]' (e.g., /spawnbattle LamiaAI 1). Default 1.";
            MyAPIGateway.Utilities.ShowMessage("spawnbattle", prefabListMessage);
        }

        private void SpawnRandomPrefabs(string targetPrefab, int spawnCount, string factionName)
        {
            double maxSpawnRadius = 10000; // Maximum spawn radius in meters

            List<Vector3D> spawnPositions = new List<Vector3D>();

            // Determine the factions to use
            IMyFaction redFaction = MyAPIGateway.Session.Factions.TryGetFactionByTag("RED");
            IMyFaction bluFaction = MyAPIGateway.Session.Factions.TryGetFactionByTag("BLU");

            IMyFaction currentFaction = factionName == "RED" ? redFaction : bluFaction;

            for (int i = 0; i < spawnCount; i++)
            {
                Vector3D origin = new Vector3D(0, 0, 0);

                // Calculate a random spawn position
                Vector3D spawnPosition = origin + (Vector3D.Normalize(MyUtils.GetRandomVector3D()) * MyUtils.GetRandomDouble(minSpawnRadiusFromCenter, maxSpawnRadius));

                // Calculate orientation vectors
                Vector3D direction = Vector3D.Normalize(origin - spawnPosition);
                Vector3D up = Vector3D.Normalize(Vector3D.Cross(direction, Vector3D.Up));

                // Check if the spawn position is valid
                bool isValidPosition = CheckAsteroidDistance(spawnPosition, minSpawnRadiusFromGrids) && CheckGridDistance(spawnPosition, minSpawnRadiusFromGrids);

                if (isValidPosition)
                {
                    // Avoid overcrowding by checking against other spawn positions
                    bool tooCloseToOtherPosition = false;
                    foreach (Vector3D existingPosition in spawnPositions)
                    {
                        if (Vector3D.Distance(existingPosition, spawnPosition) < minSpawnRadiusFromGrids)
                        {
                            tooCloseToOtherPosition = true;
                            break;
                        }
                    }

                    if (!tooCloseToOtherPosition)
                    {
                        // Spawn the prefab
                        IMyPrefabManager prefabManager = MyAPIGateway.PrefabManager;
                        List<IMyCubeGrid> resultList = new List<IMyCubeGrid>();

                        // Spawn the prefab with the current faction as the owner
                        prefabManager.SpawnPrefab(resultList, targetPrefab, spawnPosition, direction, up, ownerId: currentFaction.FounderId, spawningOptions: SpawningOptions.None);

                        // Change ownership of the spawned grids
                        foreach (IMyCubeGrid spawnedGrid in resultList)
                        {
                            spawnedGrid.ChangeGridOwnership(currentFaction.FounderId, MyOwnershipShareModeEnum.All);
                        }

                        // Add the spawn position to the list
                        spawnPositions.Add(spawnPosition);

                        // Switch to the other faction for the next spawn
                        currentFaction = currentFaction == redFaction ? bluFaction : redFaction;
                    }
                }
            }
        }




        private bool CheckGridDistance(Vector3D spawnPosition, double minDistance)
        {
            // Get all entities in the game world
            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities);

            foreach (IMyEntity entity in entities)
            {
                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid != null)
                {
                    double distance = Vector3D.Distance(spawnPosition, grid.GetPosition());

                    if (distance < minDistance)
                    {
                        return false; // Distance is too close, not a valid spawn position
                    }
                }
            }

            // Check distance from origin
            double distanceFromOrigin = Vector3D.Distance(spawnPosition, Vector3D.Zero);
            if (distanceFromOrigin < minDistance)
            {
                return false; // Distance from origin is too close, not a valid spawn position
            }

            return true; // Valid spawn position
        }

        private bool CheckAsteroidDistance(Vector3D spawnPosition, double minDistance)
        {
            // Get all asteroid entities in the game world
            List<IMyVoxelBase> voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels);

            foreach (IMyVoxelBase voxel in voxels)
            {
                if (voxel is IMyVoxelMap)
                {
                    BoundingBoxD voxelBox = voxel.PositionComp.WorldAABB;

                    if (voxelBox.Contains(spawnPosition) != ContainmentType.Disjoint)
                    {
                        return false; // Spawn position is inside an asteroid, not a valid spawn position
                    }
                }
            }

            return true; // Valid spawn position
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered; // Unsubscribe from chat message events
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(netID, NetworkHandler);
        }
    }
}