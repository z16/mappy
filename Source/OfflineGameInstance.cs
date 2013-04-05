#if OFFLINE
using MapEngine;
using mappy;
using System.Drawing;
using System.Diagnostics;

public class FFXIOfflineGameInstance : GameInstance, IFFXIMapImageContainer, IFFXIGameContainer {
   Engine m_engine;
   int aniZ = 0;
   int aniDir = 0;
   FFXIImageMaps imagemaps;
   FFXIImageMap curMap = null;
   public event System.EventHandler  ZoneChanged;
   public event System.EventHandler  MapChanged;

   public FFXIOfflineGameInstance(Engine engine, string MapFilePath) : base(engine, null, Program.ModuleName) {
      this.m_engine = engine;

      //--------------------------------------------------------
      // Create fake map data when working offline
      //--------------------------------------------------------
      engine.Data.LoadZone("fake");
      //engine.Data.LoadZone("AlZahbi");
      FakeSpawn player = new FakeSpawn(0, "YOU", SpawnType.Player, new MapPoint(0, 0, 10));
      engine.Game.Spawns.Add(player);
      engine.Game.setPlayer(player.ID, false);
      engine.Game.Spawns.Add(new FakeSpawn(1, "PLAYER", SpawnType.Player, new MapPoint(10, 10, 20), false, player));
      engine.Game.Spawns.Add(new FakeSpawn(2, "MOB", SpawnType.MOB, new MapPoint(-10, 10, 30), false, player));
      engine.Game.Spawns.Add(new FakeSpawn(3, "NPC", SpawnType.NPC, new MapPoint(-10, -10, 40), false, player));
      engine.Game.Spawns.Add(new FakeSpawn(4, "HIDDEN TYPE", SpawnType.Hidden, new MapPoint(10, -10, 50), false, player));
      engine.Game.Spawns.Add(new FakeSpawn(5, "REAL HIDDEN", SpawnType.NPC, new MapPoint(30, -10, 0), true, player));

      MapLine line;
      line = new MapLine(Color.White);
      line.Add(10, 10, -10);
      line.Add(-10, 10, -20);
      line.Add(-10, -10, -30);
      line.Add(10, -10, -40);
      line.Add(10, 10, -50);
      engine.Data.Lines.Add(line);

      line = new MapLine(Color.White);
      line.Add(30, 30, 10);
      line.Add(-30, 30, 20);
      line.Add(-30, -30, 30);
      line.Add(30, -30, 40);
      line.Add(30, 30, 50);
      engine.Data.Lines.Add(line);

      //--------------------------------------------------------
      // Testbed for offline image maps (overrides player loc)
      //--------------------------------------------------------
      Debug.WriteLine("Loading test image map...");
      imagemaps = new FFXIImageMaps(Program.MapFileExt, MapFilePath);

      //int testZone = 236; //port bastok
      //((FakeSpawn)MapEngine.Game.Player).setLocation(57.873f, -240f, 8.49999f); //mog house

      //int testZone = 107; //south gusta
      //((FakeSpawn)MapEngine.Game.Player).setLocation(577.993f, -305.077f, 0.7582641f); //mines ent

      //int testZone = 184; //lower delk
      //((FakeSpawn)engine.Game.Player).setLocation(460.772f, -103.44f, 0f); //1st floor ent
      //((FakeSpawn)MapEngine.Game.Player).setLocation(403.004f, -21.089f, 16f); //basement
      //((FakeSpawn)MapEngine.Game.Player).setLocation(376.1143f, -47.03903f, -15.42417f); //2nd floor

      //int testZone = 245; //lower jeuno
      //((FakeSpawn)engine.Game.Player).setLocation(0f, 0f, 0f);

      int testZone = 238; //EE, w.waters
      ((FakeSpawn)engine.Game.Player).setLocation(0f, 0f, 0f);

      Debug.WriteLine("Looking up map using zone " + testZone + " @ " + engine.Game.Player.Location.ToString());
      curMap = imagemaps.GetCurrentMap(testZone, engine.Game.Player.Location);
      if (curMap != null) {
         RectangleF bounds = curMap.Bounds;
         engine.MapAlternativeImage = curMap.GetImage();
         engine.MapAlternativeBounds = bounds;
         engine.Data.CheckBounds(bounds);
      } else {
         Debug.WriteLine("NO MAP");
      }

      Valid = true;

      if (engine.AutoRangeSnap)
         engine.SnapToRange();
   }

   public override bool Poll() {
      if (m_engine.Game.Player != null) {
         if (aniDir == 0) {
            aniZ += 1;
            if (aniZ > 50) {
               aniZ = 50;
               aniDir = 1;
            }
         } else {
            aniZ -= 1;
            if (aniZ < -50) {
               aniZ = -50;
               aniDir = 0;
            }
         }
         m_engine.Game.Player.Location.Z = aniZ;
         m_engine.UpdateMap();
      }
      return true;
   }

   public FFXIImageMaps Maps {
      get { return imagemaps; }
   }
   public void ResetImageMap() { }

   public FFXIImageMap CurrentMap {
      get { return curMap; }
   }
   public FFXIZoneMaps CurrentZone {
      get { return curMap.Zone; }
   }

   public void Save() {
      imagemaps.SaveMapData();
   }
}
#endif