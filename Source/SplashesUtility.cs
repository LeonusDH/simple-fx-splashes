using Verse;
using RimWorld;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;

namespace SimpleFxSplashes
{
	public class HardSurface : DefModExtension {}
	public class HardStuff : DefModExtension {}

	[StaticConstructorOnStartup]
	public static class SplashesUtility
	{
		public static HashSet<ushort> hardTerrains;
		public static Dictionary<int, Vector3[]> hardGrids = new Dictionary<int, Vector3[]>();
		public static Vector3[] activeMapHardGrid;
		public static FastRandom fastRandom = new FastRandom();
		public static FleckSystem fleckSystemCache;
		public static int splashRate = 40, arrayChunks = 0, chunkIndex = 0, adjustedSplashRate = 40, activeMapID = -1;
		//public const int splashRateMax = 50, splashRateMin = 30;
		const int chunkSize = 1000;

		//Happens once on game start, goes through the database to find defs it think is hard and rain would bounce off of
		static SplashesUtility()
		{
			List<ushort> workingList = new List<ushort>();
			List<string> report = new List<string>();
			
			HashSet<StuffCategoryDef> hardStuff = new HashSet<StuffCategoryDef>();
			for (int i = DefDatabase<StuffCategoryDef>.defsList.Count; i-- > 0;)
			{
				var thingDef = DefDatabase<StuffCategoryDef>.defsList[i];
				if (thingDef.HasModExtension<HardStuff>()) hardStuff.Add(thingDef);
			}
			
			//Go through every terrain in the game
			for (int i = DefDatabase<TerrainDef>.defsList.Count; i-- > 0;)
			{
				var terrainDef = DefDatabase<TerrainDef>.defsList[i];
				bool flag = false;
				//Does it have a cost?
				if (terrainDef.costList != null)
				{
					//Look through the costs
					for (int j = terrainDef.costList.Count; j-- > 0;)
					{
						var thingDefCountClass = terrainDef.costList[j];
						//See if any of them are metallatic or stony, which we define as hard surfaces that rain would splash off of
						if (thingDefCountClass.thingDef?.stuffProps?.categories?.Any(x => hardStuff.Contains(x)) ?? false)
						{
							flag = true;
							break;
						}
					}
				}
				else if (terrainDef.HasModExtension<HardSurface>() || terrainDef.defName.Contains("_Rough")) flag = true;
				if (flag)
				{
					workingList.Add(terrainDef.index);
					report.Add(terrainDef.label);
				}
			}
			hardTerrains = new HashSet<ushort>(workingList);
			if (Prefs.DevMode) Log.Message("[Simple FX: Splashes] The following terrains have been defined as being hard:\n - " + string.Join("\n - ", report));
		}

		public static void ProcessSplashes(Map map)
		{
			if (fastRandom.NextBool() && fastRandom.NextBool() && activeMapHardGrid != null) //This looks dumb, but it's gating more complex code behind 2 ultra-fast random bool checks.
			{
				if (fleckSystemCache == null) Find.CurrentMap.flecks.systems.TryGetValue(ResourceBank.FleckDefOf.Owl_Splash.fleckSystemClass, out fleckSystemCache);

				//Chunk start
				int chunkStart = (int)(chunkIndex * chunkSize);
				//Chunk end
				int chunkEnd = System.Math.Min(activeMapHardGrid.Length, (int)((chunkIndex * chunkSize) + chunkSize));

				for (int i = chunkStart; i < chunkEnd; ++i)
				{
					if (fastRandom.Next(adjustedSplashRate) == 0)
					{
						var splashAt = activeMapHardGrid[i];
						if (!CameraDriver.lastViewRect.Contains(splashAt.ToIntVec3())) continue;
						fleckSystemCache.CreateFleck(FleckMaker.GetDataStatic(splashAt, map, ResourceBank.FleckDefOf.Owl_Splash, ModSettings_SimpleFxSplashes.sizeMultiplier));
					}
				}
				if (++chunkIndex == arrayChunks) chunkIndex = 0;
			}
		}

		public static void RebuildCache(Map map)
		{
			//First, ensure the key is set
			if (!hardGrids.ContainsKey(map.uniqueID)) hardGrids.Add(map.uniqueID, null);

			//Generate a working list
			List<Vector3> workingList = new List<Vector3>();
			var length = map.info.NumCells;
			fastRandom.Reinitialise(map.uniqueID); //Keep random cells consisent
			for (int i = 0; i < length; i++)
            {
				//Fetch the def cell by cell
                TerrainDef terrainDef = map.terrainGrid.topGrid[i];
				//The cell must be a valid def, not roofed, and not fogged
                if (hardTerrains.Contains(terrainDef.index) && 
					(!terrainDef.natural || fastRandom.Next(100) < (ModSettings_SimpleFxSplashes.natureFilter * 100)) && 
					map.roofGrid.roofGrid[i] == null && 
					!map.fogGrid.fogGrid[i]) workingList.Add(map.cellIndices.IndexToCell(i).ToVector3Fast().RandomOffset());
            }

			//Record
			hardGrids[map.uniqueID] = workingList.ToArray();

			//Debug
			if (Prefs.DevMode) Log.Message("[Simple FX: Splashes] Splash grid build with " + workingList.Count().ToString() + " cells.");

			SetActiveGrid(map);
		}

		public static void UpdateCache(Map map, IntVec3 c, TerrainDef def = null)
		{
			fastRandom.Reinitialise(map.uniqueID); //Make sure the vectors match
			Vector3 vector = c.ToVector3Fast().RandomOffset();
			if (hardGrids.TryGetValue(map?.uniqueID ?? -1, out Vector3[] hardGrid))
			{
				//Add the new cell if relevant
				if (def == null) def = map.terrainGrid.TerrainAt(map.cellIndices.CellToIndex(c));
				bool isHard = hardTerrains.Contains(def.index) && !c.Roofed(map);
				
				//Filter out this cell
				if (isHard)
				{
					for (int i = hardGrid.Length; i-- > 0;) if (vector == hardGrid[i]) return; //No changes needed
					
					//Not found, so append a new vector3 record to the end of the array
					var newLength = hardGrid.Length + 1;
					Vector3[] replacementArray = new Vector3[newLength];
					System.Array.Copy(hardGrid, replacementArray, newLength - 1);
					replacementArray[newLength - 1] = vector;
					hardGrids[map.uniqueID] = replacementArray;
				}
				else
				{
					Vector3[] replacementArray = new Vector3[hardGrid.Length - 1];
					bool oldArrayIsDirty = false;
					for (int i = replacementArray.Length; i-- > 0;)
					{
						var tmp = hardGrid[i];
						if (vector != tmp) replacementArray[i] = tmp;
						else oldArrayIsDirty = true;
					}
					//Not found, no changes needed
					if (oldArrayIsDirty) hardGrids[map.uniqueID] = replacementArray;
					else return;
				}

				SetActiveGrid(map);
			}
		}
		
		public static void SetActiveGrid(Map map)
		{
			//Update the active grid.
			if (map != null && Find.CurrentMap?.uniqueID == map.uniqueID && hardGrids.TryGetValue(map.uniqueID, out activeMapHardGrid))
			{
				arrayChunks = (int)System.Math.Ceiling(activeMapHardGrid.Length / (float)chunkSize);
				chunkIndex = 0;
				//Adjusted splash rate
				adjustedSplashRate = (int)System.Math.Ceiling((splashRate * ModSettings_SimpleFxSplashes.splashRarity) / arrayChunks);
				activeMapID = map.uniqueID;
			}
		}

		public static void ResetCache()
		{
			hardGrids.Clear();
			activeMapHardGrid = null;
			fleckSystemCache = null;
		}
	}
}
