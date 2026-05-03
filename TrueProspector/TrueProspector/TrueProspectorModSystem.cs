using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace TrueProspector
{
    public class TrueProspectorModSystem : ModSystem
    {
        Harmony harmony;
        public static ICoreAPI Api;  // static so the patch class can access it

        public override void Start(ICoreAPI api)
        {
            Api = api;
            Mod.Logger.Notification("TrueProspector loaded on: " + api.Side);
            harmony = new Harmony("trueprospector");
            harmony.PatchAll();
        }
        public static DepositVariant[] Deposits;

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Event.SaveGameLoaded += () =>
            {
                Deposits = api.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
                Mod.Logger.Notification($"Loaded {Deposits?.Length ?? 0} deposits");
            };
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("trueprospector");
            base.Dispose();
        }
    }

    [HarmonyPatch(typeof(ItemProspectingPick), "ProbeBlockDensityMode")]
    public static class PatchDensityMode
    {
        const int ScanRadius = 16;

        static bool Prefix(
            IWorldAccessor world,
            Entity byEntity,
            ItemSlot itemslot,
            BlockSelection blockSel)
        {
            PropickReading reading = new PropickReading();


            IPlayer byPlayer = null;
            if (byEntity is EntityPlayer) byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

            Block block = world.BlockAccessor.GetBlock(blockSel.Position);
            float dropMul = 1f;
            if (block.BlockMaterial == EnumBlockMaterial.Ore || block.BlockMaterial == EnumBlockMaterial.Stone) dropMul = 0;

            //block.OnBlockBroken(world, blockSel.Position, byPlayer, dropMul);

            if (!isPropickable(block)) return false;

            IServerPlayer splr = byPlayer as IServerPlayer;
            if (splr == null) return false;

            // Damage item immediately on main thread
            itemslot.Itemstack.Collectible.DamageItem(world, byEntity, itemslot, 1);
            itemslot.MarkDirty();

            // Capture everything the background thread needs before handing off
            BlockPos center = blockSel.Position.Copy();
            int maxY = world.BlockAccessor.MapSizeY;
            int regsize = world.BlockAccessor.RegionSize;
            IMapRegion reg = world.BlockAccessor.GetMapRegion(center.X / regsize, center.Z / regsize);
            if (reg == null) return false;

            // Build intersection whitelist on main thread before handing off
            var detectableOres = new HashSet<string>(reg.OreMaps.Keys);
            if (TrueProspectorModSystem.Deposits != null)
                detectableOres.IntersectWith(TrueProspectorModSystem.Deposits.Select(d => d.Code));

            var detectedOres = new HashSet<string>();

            TyronThreadPool.QueueTask(() =>
            {
                var oreCounts = new Dictionary<string, int>();
                int totalBlocks = 0;

                world.BlockAccessor.WalkBlocks(
                    center.AddCopy(-ScanRadius, -center.Y, -ScanRadius),
                    center.AddCopy(ScanRadius, maxY - center.Y, ScanRadius),
                    (b, x, y, z) =>
                    {
                        if (b == null || b.Id == 0) return;

                        int dx = x - center.X;
                        int dz = z - center.Z;
                        if (dx * dx + dz * dz > ScanRadius * ScanRadius) return;

                        if (b.BlockMaterial != EnumBlockMaterial.Ore &&
                            b.BlockMaterial != EnumBlockMaterial.Stone) return;

                        totalBlocks++;

                        string groupKey = b.BlockMaterial == EnumBlockMaterial.Ore
    ? b.Variant?["type"]
    : b.Variant?["rock"];

                        detectedOres.Add(groupKey);
                        if (!detectableOres.Contains(groupKey)) return;

                        if (!oreCounts.ContainsKey(groupKey))
                            oreCounts[groupKey] = 0;
                        oreCounts[groupKey]++;
                    }
                );

                // Marshal results back to main thread for API calls
                world.Api.Event.EnqueueMainThreadTask(() =>
                {

                    splr.SendMessage(
                        GlobalConstants.InfoLogChatGroup,
                        $"Detectable ores: {string.Join(", ", detectableOres)}",
                         EnumChatType.Notification
                        );

                    splr.SendMessage(
                        GlobalConstants.InfoLogChatGroup,
                        $"Detected ores: {string.Join(", ", detectedOres)}",
                         EnumChatType.Notification
                        );

                    if (oreCounts.Count == 0)
                    {
                        splr.SendMessage(
                            GlobalConstants.InfoLogChatGroup,
                            $"No ores found within {ScanRadius} block radius.",
                            EnumChatType.Notification
                        );
                    }
                    else
                    {
                        splr.SendMessage(
                            GlobalConstants.InfoLogChatGroup,
                            $"Ores present within {ScanRadius} block radius:",
                            EnumChatType.Notification
                        );

                        foreach (var kvp in oreCounts)
                        {
                            double permil = totalBlocks > 0 ? (kvp.Value / (double)totalBlocks) * 1000.0 : 0;
                            

                            splr.SendMessage(
                                GlobalConstants.InfoLogChatGroup,
                                $"  {kvp.Key}: {permil:F2}‰",
                                EnumChatType.Notification
                            );
                        }
                    }
                }, "trueprospector-results");

            }, "trueprospector-scan");

            return true;
        }

        // Duplicates vssurvivalmod/Systems/Prospecting/ItemProspectingPick.cs::isPropickable
        private static bool isPropickable(Block block)
        {
            return block?.Attributes?["propickable"].AsBool(false) == true;
        }
    }
}