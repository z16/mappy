using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Diagnostics;

namespace MapEngine {
   /// <summary>A container for the current game state.</summary>
   public class GameData {
      private GameSpawns m_spawns;
      private GameSpawn m_player;
      private GameSpawn m_target;
      private GameSpawn m_selected;
      private GameSpawn m_highlighted;
      private Engine m_engine;

      /// <summary>Fires when the game state has changed.</summary>
      public event GenericEvent Updated;

      public GameData(Engine engine) {
         m_spawns = new GameSpawns(this);
         m_player = null;
         m_target = null;
         m_engine = engine;
      }

      /// <summary>Gets the array of spawns currently associated with the process.</summary>
      public GameSpawns Spawns {
         get { return m_spawns; }
      }

      /// <summary>Gets the engine bound to this data pool.</summary>
      public Engine Engine {
         get { return m_engine; }
      }

      /// <summary>Gets or sets the spawn that is recognized to be the logged in player.</summary>
      public GameSpawn Player {
         get { return m_player; }
         set { m_player = value; }
      }
      /// <summary>Gets or sets the spawn that is recognized to be the current target.</summary>
      public GameSpawn Target {
         get { return m_target; }
         set { m_target = value; }
      }
      /// <summary>Gets or sets the spawn that is selected.</summary>
      public GameSpawn Selected {
         get { return m_selected; }
         set { m_selected = value; }
      }
      /// <summary>Gets or sets the spawn that is highlighed.</summary>
      public GameSpawn Highlighted {
         get { return m_highlighted; }
         set { m_highlighted = value; }
      }

      /// <summary>Clears all spawn information</summary>
      public void Clear() {
         m_spawns.Clear();
         m_target = null;
         m_player = null;
         m_selected = null;
         m_highlighted = null;
         Update();
      }

      /// <summary>Forces each spawn to update itself and then causes the map to redraw.</summary>
      public void Update() {
         foreach (KeyValuePair<uint, GameSpawn> spawn in m_spawns) {
            //force the spawn to update itself
            spawn.Value.Update();

            //check to make sure the spawn is within the map boundary. if not, then expand it
            if (!spawn.Value.Hidden || m_engine.ShowHiddenSpawns)
               m_engine.Data.CheckBatch(spawn.Value.Location.X, spawn.Value.Location.Y);
         }
         //force the boundary to recalculate now that were done checking them all
         m_engine.Data.CheckBatchEnd();

         //notify the parent control that we need to refresh the map
         if (Updated != null)
            Updated();
      }

      /// <summary>
      /// Sets the player spawn by its ID
      /// </summary>
      /// <param name="ID">The ID of the spawn</param>
      /// <param name="batch">If true, the map will not automatically update</param>
      public void setPlayer(uint ID, bool batch) {
         if (Player != null && Player.ID == ID)
            return;
         if (m_spawns.ContainsID(ID))
            Player = m_spawns[ID];
         if (!batch && Updated != null)
            Updated();
      }

      /// <summary>
      /// Sets the target spawn by its ID
      /// </summary>
      /// <param name="ID">The ID of the spawn</param>
      /// <param name="batch">If true, the map will not automatically update</param>
      public void setTarget(uint ID, bool batch) {
         if (Target != null && Target.ID == ID)
            return;

         if (ID == 0) {
            Target = null;
         } else if (m_spawns.ContainsID(ID)) {
            Target = m_spawns[ID];
         }

         if (!batch && Updated != null)
            Updated();
      }
   }

   /// <summary>A collection of game spawns.</summary>
   public class GameSpawns {
      private Dictionary<uint, GameSpawn> m_spawns;
      private GameData m_data;

      public GameSpawns(GameData data) {
         m_spawns = new Dictionary<uint, GameSpawn>();
         m_data = data;
      }

      /// <summary>Gets the spawn based on its ID.</summary>
      public GameSpawn this[uint idx] {
         get { return m_spawns[idx]; }
      }

      /// <summary>Determines if a spawn exists in the collection with the given ID.</summary>
      public bool ContainsID(uint ID) {
         return m_spawns.ContainsKey(ID);
      }

      public Dictionary<uint, GameSpawn>.Enumerator GetEnumerator() {
         return m_spawns.GetEnumerator();
      }

      /// <summary>Clears all spawn data.</summary>
      public void Clear() {
         m_spawns.Clear();
      }

      /// <summary>Gets the number of spawns in the collection.</summary>
      public int Count {
         get { return m_spawns.Count; }
      }

      /// <summary>Adds the spawn to the collection.</summary>
      public void Add(GameSpawn spawn) {
         if (!m_spawns.ContainsKey(spawn.ID)) {
            m_spawns.Add(spawn.ID, spawn);
            m_data.Engine.Data.Hunts.Bind(spawn);
            m_data.Engine.Data.Replacements.Bind(spawn);
         }
      }

      /// <summary>
      /// Attempts to find the spawn at the specified MAP coordinate.
      /// </summary>
      /// <param name="x">The X MAP coordinate</param>
      /// <param name="y">The Y MAP coordinate</param>
      /// <param name="threshhold">Search tolerance</param>
      /// <returns>The closest spawn to the specified map coordinates, or null if there are no spawns within the search tolerance.</returns>
      public GameSpawn FindSpawn(float x, float y, float threshhold) {
         float     closestDistance = -1;
         GameSpawn closestSpawn    = null;

         foreach(KeyValuePair<uint, GameSpawn> pair in m_spawns) {
            if(!pair.Value.Hidden || m_data.Engine.ShowHiddenSpawns) {
               //calculate the distance between where the mouse cursor is and the center of the spawn
               float distance = CalcDistance(pair.Value, x, y);
               
               //if this is currently the closest then cache the spawn
               if(closestDistance < 0 || distance < closestDistance) {
                  closestDistance = distance;
                  closestSpawn = pair.Value;
               }
            }
         }

         //if closest spawn is within the requested threshold then return it
         if(closestDistance > 0 && closestDistance < threshhold)
            return closestSpawn;
         return null;
      }

      /// <summary>Calculate the distance between a spawn and an arbitrary set of MAP coordinates.</summary>
      public float CalcDistance(GameSpawn source, float x, float y) {
         return CalcDistance(source.Location.X, source.Location.Y, x, y);
      }

      /// <summary>Calculate the distance between two spawns</summary>
      public float CalcDistance(GameSpawn source, GameSpawn dest) {
         return CalcDistance(source.Location.X, source.Location.Y, dest.Location.X, dest.Location.Y);
      }

      /// <summary>Calculate the distance between two MAP points</summary>
      public float CalcDistance(float x1, float y1, float x2, float y2) {
         float s1 = x1 - x2;
         float s2 = y1 - y2;
         return (float)Math.Sqrt(s1 * s1 + s2 * s2); //go go pythagorean theorem
      }
   }

   public enum SpawnType : int {
      Player = 0,
      NPC = 1,
      MOB = 2,
      Hidden = 3
   }

   /// <summary>This is a base prototype intended to be extended and used by game specific containers</summary>
   public class GameSpawn {
      private uint      m_id          = 0;
      private string    m_name        = "";
      private MapPoint  m_location;
      private float     m_heading     = 0;
      private float     m_distance    = 0;
      private float     m_speed       = 0;
      private SpawnType m_type        = SpawnType.MOB;
      private int       m_hpp         = 0;
      private int       m_level       = 0;
      private bool      m_dead        = false;
      private bool      m_combat      = false;
      private bool      m_hidden      = true;
      private bool      m_alert       = false;
      private bool      m_hunt        = false;
      private bool      m_replacement = false;
      private string    m_repName     = "";
      private Image     m_icon        = null;
      private Color     m_colorFill   = Color.Black;
      private uint      m_claimID     = 0;
      private uint      m_petID       = 0;
      private bool      m_groupMember = false;
      private bool      m_raidMember  = false;
      private string    m_DEBUG       = "";
      private string    m_DEBUGHOVER  = "";
      private bool      m_attackable  = false;

      protected GameSpawn() {
         m_location = new MapPoint();
      }
      public GameSpawn(uint ID) {
         m_id = ID;
         m_location = new MapPoint();
      }
      public uint ID {
         get { return m_id; }
         protected set { m_id = value; }
      }
      public string Name {
         get { return m_name; }
         protected set { m_name = value; }
      }
      public MapPoint Location {
         get { return m_location; }
      }
      protected void setLocation(float x, float y, float z) {
         m_location.Set(x, y, z);
      }
      public float Heading {
         get { return m_heading; }
         protected set { m_heading = value; }
      }
      public float Distance {
         get { return m_distance; }
         protected set { m_distance = value; }
      }
      public float Speed {
         get { return m_speed; }
         protected set { m_speed = value; }
      }
      public bool Dead {
         get { return m_dead; }
         protected set { m_dead = value; }
      }
      public bool InCombat {
         get { return m_combat; }
         protected set { m_combat = value; }
      }
      public bool Alert {
         get { return m_alert; }
         set { m_alert = value; }
      }
      public bool Hunt {
         get { return m_hunt; }
         set { m_hunt = value; }
      }
      public bool Replacement
      {
          get { return m_replacement; }
          set { m_replacement = value; }
      }
      public string RepName
      {
          get { return m_repName; }
          set { m_repName = value; }
      }
      public bool Hidden {
         get { return m_hidden; }
         protected set { m_hidden = value; }
      }
      public SpawnType Type {
         get { return m_type; }
         protected set { m_type = value; }
      }
      public int Level {
         get { return m_level; }
         protected set { m_level = value; }
      }
      public int HealthPercent {
         get { return m_hpp; }
         protected set { m_hpp = value; }
      }
      public Image Icon {
         get { return m_icon; }
         protected set { m_icon = value; }
      }
      public Color FillColor {
         get { return m_colorFill; }
         protected set { m_colorFill = value; }
      }
      public uint ClaimID {
         get { return m_claimID; }
         protected set { m_claimID = value; }
      }
      public uint PetID {
         get { return m_petID; }
         protected set { m_petID = value; }
      }
      public bool isRaidMember {
         get { return m_raidMember; }
         protected set { m_raidMember = value; }
      }
      public bool isGroupMember {
         get { return m_groupMember; }
         protected set { m_groupMember = value; }
      }
      public bool isAttackable {
         get { return m_attackable; }
         protected set { m_attackable = value; }
      }

      public string DEBUG {
         get { return m_DEBUG; }
         set { m_DEBUG = value; }
      }

      public string DEBUGHOVER {
         get { return m_DEBUGHOVER; }
         set { m_DEBUGHOVER = value; }
      }

      //prototype for derived classes
      public virtual void Update() { }
   }

#if OFFLINE
   /// <summary>FakeSpawn is an offline development helper to quickly stamp out spawns to render.</summary>
   public class FakeSpawn : GameSpawn {
      GameSpawn fp;

      public FakeSpawn(uint ID, string Name, SpawnType Type, MapPoint Location) : this(ID, Name, Type, Location, false, null) { }
      public FakeSpawn(uint ID, string Name, SpawnType Type, MapPoint Location, bool Hidden) : this(ID, Name, Type, Location, Hidden, null) { }
      public FakeSpawn(uint ID, string Name, SpawnType Type, MapPoint Location, bool Hidden, GameSpawn fakeplayer) {
         base.ID = ID;
         base.Name = Name;
         base.Type = Type;
         base.setLocation(Location.X, Location.Y, Location.Z);
         base.Hidden = Hidden;
         fp = fakeplayer;
         Update();
      }
      new public void setLocation(float x, float y, float z) {
         base.setLocation(x, y, z);
      }

      override public void Update() {
         if (fp != null)
            base.Distance = (float)this.Location.calcDist2D(fp.Location);
      }
   }
#endif
}