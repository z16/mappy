using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using MapEngine;
using System.Threading;
using System.IO;
using System.Windows.Forms;
using System.Globalization;

////////////////////////////////////////////////////////////////////////////////////
// All image-based map data is stored in FFAssist format.
// Formulas, map packs, and format information derived from:
// http://forums.windower.net/topic/7563-map-pack-nov-2-and-mapini-dec-15/
////////////////////////////////////////////////////////////////////////////////////
namespace mappy
{
    public class FFXIGameInstance : GameInstance, IFFXIGameContainer, IFFXIMapImageContainer
    {
        //assembler code to scan for in order to locate pointers to data
        //public static readonly string DEFAULT_SIG_ZONE_ID     = ">>0fbfc88bc1894e??a3";
        public static readonly string DEFAULT_SIG_ZONE_ID = "7CE18B4E088B15";
        public static readonly string DEFAULT_SIG_ZONE_SHORT = "5f5ec38b0cf5";
        public static readonly string DEFAULT_SIG_SPAWN_START = "<<8b3e3bfb74128bcfe8";
        public static readonly string DEFAULT_SIG_SPAWN_END = "891e83c60481fe";
        public static readonly string DEFAULT_SIG_MY_ID = "8B8EA800000051B9";
        public static readonly string DEFAULT_SIG_MY_TARGET = "8946188b0d????????85c9";
        //public static readonly string DEFAULT_SIG_MY_TARGET = "3ac3a1????????8a4838"; //target was the only one to break last patch. including this backup just in case it happens again.

        public static string SIG_ZONE_ID = DEFAULT_SIG_ZONE_ID;
        public static string SIG_ZONE_SHORT = DEFAULT_SIG_ZONE_SHORT;
        public static string SIG_SPAWN_START = DEFAULT_SIG_SPAWN_START;
        public static string SIG_SPAWN_END = DEFAULT_SIG_SPAWN_END;
        public static string SIG_MY_ID = DEFAULT_SIG_MY_ID;
        public static string SIG_MY_TARGET = DEFAULT_SIG_MY_TARGET;
        private static bool sigloaded = false;

        private List<string> m_zoneNameShort;
        private FFXIImageMaps m_imagemaps;

        private IntPtr pSpawnStart;
        private IntPtr pSpawnEnd;
        private IntPtr pMyID;
        private IntPtr pMyTarget;
        private IntPtr pZoneID;
        private IntPtr pZoneShortNames;
        private int listSize;
        private int listMax;
        private int lastZone;
        private int lastMapID = -1;
        private bool zoneFinished = false;
        private Dictionary<UInt32, FFXISpawn> m_ServerIDLookup;
        private MapPoint lastPlayerLocation;
        FFXIImageMap curMap = null;

        public event EventHandler ZoneChanged;
        public event EventHandler MapChanged;

        public FFXIGameInstance(Engine engine, Config config, string FilePath, string ModuleName)
            : base(engine, config, ModuleName)
        {
            m_ServerIDLookup = new Dictionary<uint, FFXISpawn>();
            m_zoneNameShort = new List<string>();
            lastZone = 0;

            FFXIGameInstance.LoadSignatures(config);
            m_imagemaps = new FFXIImageMaps(Program.MapFileExt, FilePath);
        }

        public FFXIGameInstance(Process process, Engine engine, Config config, string FilePath, string ModuleName)
            : base(engine, config, ModuleName)
        {
            m_ServerIDLookup = new Dictionary<uint, FFXISpawn>();
            m_zoneNameShort = new List<string>();
            lastZone = 0;

            FFXIGameInstance.LoadSignatures(config);
            m_imagemaps = new FFXIImageMaps(Program.MapFileExt, FilePath);
            Process = process;
        }

        private static void LoadSignatures(Config config)
        {
            if (sigloaded)
                return;

            //Determine if there are manually configured signatures
            if (config.Exists("sigversion"))
            {
                //If so, first determine if there are new internally built ones and clobber the manual ones if so.
                if (config.Get("sigversion", "") != Application.ProductVersion)
                {
                    config.Remove("sigversion");
                    config.Remove("SIG_MY_ID");
                    config.Remove("SIG_MY_TARGET");
                    config.Remove("SIG_SPAWN_END");
                    config.Remove("SIG_SPAWN_START");
                    config.Remove("SIG_ZONE_ID");
                    config.Remove("SIG_ZONE_SHORT");
                }

                //push the manually configured signatures
                SIG_MY_ID = config.Get("SIG_MY_ID", DEFAULT_SIG_MY_ID);
                SIG_MY_TARGET = config.Get("SIG_MY_TARGET", DEFAULT_SIG_MY_TARGET);
                SIG_SPAWN_END = config.Get("SIG_SPAWN_END", DEFAULT_SIG_SPAWN_END);
                SIG_SPAWN_START = config.Get("SIG_SPAWN_START", DEFAULT_SIG_SPAWN_START);
                SIG_ZONE_ID = config.Get("SIG_ZONE_ID", DEFAULT_SIG_ZONE_ID);
                SIG_ZONE_SHORT = config.Get("SIG_ZONE_SHORT", DEFAULT_SIG_ZONE_SHORT);
            }
            sigloaded = true;
        }

        public FFXIImageMaps Maps
        {
            get { return m_imagemaps; }
        }

        /// <summary>Find the zone id from the specified server id.</summary>
        public uint lookupZoneIDFromServerID(UInt32 ServerID)
        {
            if (m_ServerIDLookup.ContainsKey(ServerID))
                return m_ServerIDLookup[ServerID].ID;
            return 0;
        }

        /// <summary>Reload the map image alternative deck.</summary>
        public void ReloadImageMaps()
        {
            ResetImageMap();
            m_imagemaps.Reload();
        }

        public void ResetImageMap()
        {
            engine.MapAlternativeImage = null;
            lastPlayerLocation = null;
            lastMapID = -1;
        }

        /// <summary>Extracts the abbreviated zone text using the specified pointer and builds the lookup table.</summary>
        /// <param name="pTable">A pointer to the pointer table. derived from asm code.</param>
        private void ProcessZoneNames(IntPtr pTable)
        {
            //How things are stored:
            //  there is a pointer table that contains the pointers to the offset table and string table.
            //  the offset table is a serial list of paired integers: [offset][length] x 256
            //  the offset is a hard offset from the string table base address; the length the number of bytes to read.
            //  However, the actual zone text is inset by 40 bytes (for reasons unknown), so that must be accounted for.

            //Read in the pointer table
            m_zoneNameShort.Clear();
            Int32[] pList = reader.ReadStructArray<Int32>(pTable, 4); //this pointer is derived from ASM code. why it includes
            Int32 offsetTableBase = pList[2];                         // the first 2 garbage entries and not the "full text"
            Int32 stringTableBase = pList[3];                         // table instead, i dont know.

            //prime ze pump
            Int32 index = 0;
            Int32 currentOffset = reader.ReadStruct<Int32>((IntPtr)offsetTableBase);
            Int32 currentLength = reader.ReadStruct<Int32>((IntPtr)(offsetTableBase + 4));
            Int32 lastOffset = -1;
            int stringOffset = 0x28; //string is always inset by 40 bytes
            string zoneText = "";

            //Loop through each offset and only stop if the next offset is less than the last...
            //  zone information is stored in a byte, and thus there are only 256 maximum zone entries. period.
            //  If a later expansion exceeds this amount, it is quite possible SE will convert this to a short.
            //  so this way of pulling the data out is an attempt to be future proof.
            while (currentOffset > lastOffset)
            {
                //read the string at the specified table offset
                zoneText = reader.ReadString((IntPtr)(stringTableBase + currentOffset + stringOffset), currentLength - stringOffset);
                //add it to the pile.
                m_zoneNameShort.Add(zoneText);
                //read the next pair
                index++;
                lastOffset = currentOffset;
                currentOffset = reader.ReadStruct<Int32>((IntPtr)(offsetTableBase + (index * 8)));
                currentLength = reader.ReadStruct<Int32>((IntPtr)(offsetTableBase + (index * 8) + 4));
            }
        }

        //This is a pair of functions to allow the user to save changes to the map even after zoning.
        //  Becuase the zone change is performed during polling, this must be offloaded into another thread.
        //  Otherwise the next interval will raise the zoning code again while it waits for the user to answer.
        private void DirtyZone(MapData data)
        {
            //start the thread
            Thread thread = new Thread(DirtyZoneThread);
            thread.Start(data);
        }
        private void DirtyZoneThread(object data)
        {
            MapData copy = (MapData)data;
            if (System.Windows.Forms.MessageBox.Show(Program.GetLang("msg_unsaved_text"), Program.GetLang("msg_unsaved_title"), System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Exclamation, System.Windows.Forms.MessageBoxDefaultButton.Button1) == System.Windows.Forms.DialogResult.Yes)
            {
                copy.Save();
            }
        }

        protected override void OnInitializeProcess()
        {
            try
            {
                //These are direct addresses to things used by the instance manager
                pSpawnStart = reader.FindSignature(SIG_SPAWN_START, Program.ModuleName);
                pSpawnEnd = reader.FindSignature(SIG_SPAWN_END, Program.ModuleName);
                pMyID = reader.FindSignature(SIG_MY_ID, Program.ModuleName, 4);
                pZoneID = reader.FindSignature(SIG_ZONE_ID, Program.ModuleName);

                //This is a double pointer to the target table. the table is laid out by:
                // 0x00 Zone ID
                // 0x04 Server ID (null if not a player)
                // 0x08 p->Spawn Record
                IntPtr ppMyTarget = reader.FindSignature(SIG_MY_TARGET, Program.ModuleName);

                //This is a double pointer to the zone name table
                IntPtr ppZoneShortNames = reader.FindSignature(SIG_ZONE_SHORT, Program.ModuleName, 0xA0); //offset by 0xA0
                pMyTarget = IntPtr.Zero;
                pZoneShortNames = IntPtr.Zero;

                if (ppMyTarget != IntPtr.Zero)
                    pMyTarget = (IntPtr)reader.ReadStruct<Int32>(ppMyTarget);
                if (ppZoneShortNames != IntPtr.Zero)
                    pZoneShortNames = (IntPtr)reader.ReadStruct<Int32>(ppZoneShortNames);

#if DEBUG
            //Quick grabbag of pointers for memory digging
            Debug.WriteLine("Signature Addressess:");
            Debug.WriteLine("Spawn Start: " + pSpawnStart.ToString("X"));
            Debug.WriteLine("Spawn End: " + pSpawnEnd.ToString("X"));
            Debug.WriteLine("My ID: " + pMyID.ToString("X"));
            Debug.WriteLine("*My Target: " + ppMyTarget.ToString("X"));
            Debug.WriteLine("My Target: " + pMyTarget.ToString("X"));
            Debug.WriteLine("Zone ID: " + pZoneID.ToString("X"));
            Debug.WriteLine("*Zone Names (short): " + ppZoneShortNames.ToString("X"));
            Debug.WriteLine("Zone Names (short): " + pZoneShortNames.ToString("X"));
#endif

                if (pSpawnStart == IntPtr.Zero || pSpawnEnd == IntPtr.Zero || pMyID == IntPtr.Zero ||
                   pZoneID == IntPtr.Zero || pMyTarget == IntPtr.Zero || pZoneShortNames == IntPtr.Zero)
                {
                    string failtext = "[FAILED]";
                    string varlist = "Spawn List Start: " + (pSpawnStart == IntPtr.Zero ? failtext : pSpawnStart.ToString("X2"));
                    varlist += "\nSpawn List End: " + (pSpawnEnd == IntPtr.Zero ? failtext : pSpawnEnd.ToString("X2"));
                    varlist += "\nMy ID: " + (pMyID == IntPtr.Zero ? failtext : pMyID.ToString("X2"));
                    varlist += "\nMy Target: " + (pMyTarget == IntPtr.Zero ? failtext : pMyTarget.ToString("X2"));
                    varlist += "\nZone ID: " + (pZoneID == IntPtr.Zero ? failtext : pZoneID.ToString("X2"));
                    varlist += "\nZone Names (short): " + (pZoneShortNames == IntPtr.Zero ? failtext : pZoneShortNames.ToString("X2"));
                    throw new InstanceException(string.Format(Program.GetLang("msg_invalid_sig_text"), Process.MainWindowTitle, varlist), InstanceExceptionType.SigFailure);
                }

                //cache the size and index count of the array
                listSize = (int)pSpawnEnd - (int)pSpawnStart;
                if (listSize % 4 != 0)
                    return;
                listMax = listSize / 4; //size of a 32bit pointer

                //Build the zone name table
                ProcessZoneNames(pZoneShortNames);
                Valid = true;
            }
            catch (InstanceException fex)
            {
                throw fex; //cascade the exception to the caller
            }
            catch (MemoryReaderException fex)
            {
                throw fex; //cascade the exception to the caller
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error while initializing the process: " + ex.Message);
            }
        }

        public override bool Poll()
        {
            if (Switching)
                return true;
            if (reader == null)
                return false;
            if (reader.HasExited)
                return false;
            if (!Valid)
                throw new InstanceException("FFXI could not be polled becuase the context is invalid.", InstanceExceptionType.InvalidContext);

            try
            {
                //grab the current zone id
                Int32 ZoneID = reader.ReadStruct<Int32>(pZoneID);
                if (ZoneID > 0xFF)
                    ZoneID = ZoneID - 0x1BC;

                //stop all reading while the zone is in flux
                if (ZoneID == 0)
                {
                    lastZone = -1; //make sure the data gets properly reset in case the player zones into the same zone (tractor, warp, etc)
                    lastMapID = -1;
                    return true;
                }

                //If the zone has changed, then clear out any old spawns and load the zone map
                if (ZoneID != lastZone || engine.Data.Empty)
                {
                    //An edit to the map has been made. Inform the user they are about to lose thier changes
                    //  and give them a final opportunity to save them.
                    if (engine.Data.Dirty)
                    {
                        //clone the map data
                        MapData mapcopy = engine.Data.Clone();
                        //pass the closed data to another thread so the current zone can continue processing.
                        DirtyZone(mapcopy);
                    }

                    zoneFinished = false;

                    //get the zone name
                    string shortName = "";
                    if (ZoneID < m_zoneNameShort.Count)
                        shortName = m_zoneNameShort[ZoneID];    //grab the new zone name
                    if (shortName == "")
                        shortName = "Zone" + ZoneID.ToString(); //support unnamed zones, like the mog house

                    //release zone resources consumed by the image map processor
                    if (engine.ShowMapAlternative)
                    {
                        engine.MapAlternativeImage = null;
                        m_imagemaps.ClearCache(lastZone);
                    }

                    //clear the old zone data and load in the new
                    engine.Clear(); //clear both the spawn and map data
                    engine.Data.ZoneName = shortName;
                    engine.Data.LoadZone(shortName); //load the zone map
                    lastZone = ZoneID;
                    lastMapID = -1;
                }

                //read the pointer array in one big lump, and create spawns for any new id's detected
                Int32[] spawnList = reader.ReadStructArray<Int32>(pSpawnStart, listMax); //cant use intptr since its machine dependant
                for (uint i = 0; i < listMax; i++)
                {
                    if (spawnList[i] > 0)
                    {
                        //only add new id's. each spawn is responsible for updating itself.
                        if (!engine.Game.Spawns.ContainsIndex(i))
                        {
                            //create the spawn and add it to the game data
                            FFXISpawn spawn = new FFXISpawn(i, (IntPtr)spawnList[i], this);

                            engine.Game.Spawns.Add(spawn);

                            //spawn.DEBUGHOVER = "pointer: " + spawnList[i].ToString("X") + " index: " + i;

                            //add the spawn to the server lookup table. this is used later to convert the claim id
                            if (spawn.Type == SpawnType.Player)
                            {
                                if (m_ServerIDLookup.ContainsKey(spawn.ServerID))
                                    m_ServerIDLookup[spawn.ServerID] = spawn;
                                else
                                    m_ServerIDLookup.Add(spawn.ServerID, spawn); //add the server id to the lookup table
                            }
                        }
                    }
                }

                //fill in the player and target
                UInt32 myID = reader.ReadStruct<UInt32>(pMyID);
                engine.Game.setPlayer(myID, true);
                UInt32 myTarget = reader.ReadStruct<UInt32>(pMyTarget);
                engine.Game.setTarget(myTarget, true);

                //force each spawn to self update
                engine.Game.Update();

                //determine if the map image alternative requires processing
                if (engine.ShowMapAlternative && (lastPlayerLocation == null || (engine.Game.Player != null && !engine.Game.Player.Location.isEqual(lastPlayerLocation))))
                {
                    //Since the player has moved, determine if the map id has changed
                    lastPlayerLocation = engine.Game.Player.Location.Clone();
                    curMap = m_imagemaps.GetCurrentMap(ZoneID, lastPlayerLocation);

                    //only process if there is a map to display
                    if (curMap != null)
                    {
                        /// IHM EDIT
                        //Send the location in image coordinates to the engine. (I don't like doing it this way.)
                        engine.LocInImage = curMap.Translate(engine.Game.Player.Location);

                        //only process if the map has actually changed
                        if (curMap.MapID != lastMapID)
                        {
                            engine.MapAlternativeImage = curMap.GetImage(); //set the background image
                            RectangleF bounds = curMap.Bounds;                //retrieve the map coodinate boundaries
                            engine.MapAlternativeBounds = bounds;           //set the origin/scale of the background image
                            engine.Data.CheckBounds(bounds);                //expand the map bounds (if necessary) to allow the map to be zoomed all the way out
                            lastMapID = curMap.MapID;                         //set the map id so that the map isnt processed again until a change is made
                            if (MapChanged != null)
                                MapChanged(curMap, new EventArgs());
                        }
                    }
                    else if (engine.MapAlternativeImage != null)
                    {
                        //inform the engine that there is no map to display for the current location
                        engine.MapAlternativeImage = null;
                        lastMapID = -1;
                    }
                }

                if (engine.Game.Spawns.Count > 0 && !zoneFinished)
                {
                    zoneFinished = true;

                    //automatically snap the range into view (if enabled)
                    if (engine.AutoRangeSnap)
                        engine.SnapToRange();
                    if (ZoneChanged != null)
                        ZoneChanged(curMap, new EventArgs());
                }
                return true;
#if DEBUG
         } catch(Exception ex) {
            Debug.WriteLine("Error while polling the process: " + ex.Message);
#else
            }
            catch
            {
#endif
                return false;
            }
        }

        public FFXIImageMap CurrentMap
        {
            get { return curMap; }
        }
        public FFXIZoneMaps CurrentZone
        {
            get
            {
                if (curMap != null)
                    return curMap.Zone;
                return null;
            }
        }

        public void Save()
        {
            m_imagemaps.SaveMapData();
        }
    }

    //Biggest. Struct. Ever.
    [StructLayout(LayoutKind.Sequential, Pack = 1)] //im sure some of these fields could be removed with the proper packing. but im lazy.
    public struct SpawnInfo
    {
        public Int32 u1; //possibly a signature. always seems to be this for player records. other bytes noticed here for different data types in the linked list.
        public float PredictedX; //These coords jump. a LOT. This leaves me to believe they are predicted values by the client.
        public float PredictedZ; //  Prediction is rooted in FPS games to give the user a smoother movement experience.
        public float PredictedY; //  Lag in whitegate will GREATLY demonstrate the properties of these values if used.
        public float u2;
        public Int32 u3;
        public float PredictedHeading;
        public Int32 u4;
        public float u5;
        public float X; //These coords are used because it seems like a good mix between actual and predicted.
        public float Z; //Also note that the assinine ordering (xzy) is corrected.
        public float Y;
        public float u6;
        public Int32 u7;
        public float Heading; //heading is expressed in radians. cos/sin will extract a normalized x/y coord.
        public Int32 u8;
        public Int32 u9;
        public float X2; //These are assumed to be server confirmed (actual) coords.
        public float Z2;
        public float Y2;
        public float u10;
        public Int32 u11;
        public Int32 u12;
        public Int32 u13;
        public Int32 u14;
        public Int32 u15;
        public Int32 u16;
        public Int32 u17;
        public Int32 u18;
        public UInt32 ZoneID;
        public UInt32 ServerID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)]
        public string DisplayName; //player is 16 max, but npc's can be up to 24.
        public Int32 pUnknown;
        public float RunSpeed;
        public float AnimationSpeed;
        public Int32 pListNode; //a pointer to the node this record belongs to in a resource linked list. note that the data in this list contains many things not just spawn data. further down the chain is unstable.
        public Int32 u19;
        public Int32 m01; //March 2013 Update
        public Int32 NPCTalking;
        public float Distance;
        public Int32 u20;
        public Int32 u21;
        public float Heading2;
        public Int32 pPetOwner; //only for permanent pets. charmed mobs do not fill this.
        public Int32 TP;
        public byte HealthPercent;
        public byte ManaPercent;
        public byte u22;
        public byte ModelType;
        public byte Race;
        public byte u23;
        public Int16 u24;
        public Int32 u25;
        public Int64 u26;
        public Int16 ModelFace;
        public Int16 ModelHead;
        public Int16 ModelBody;
        public Int16 ModelHands;
        public Int16 ModelLegs;
        public Int16 ModelFeet;
        public Int16 ModelMain;
        public Int16 ModelSub;
        public Int16 ModelRanged;
        public Int16 u27;
        public Int32 u28;
        public Int32 u29;
        public Int32 u30;
        public Int16 u31;
        public byte u32;
        public byte u33;
        public Int32 u34;
        public int Flags1;
        public int Flags2;
        public int Flags3;
        public int Flags4;
        public int Flags5;
        public int Flags6;
        public Int32 u35;
        public Int16 u36;
        public Int16 NPCSpeechLoop;
        public Int16 NPCSpeechFrame;
        public Int16 u37;
        public Int32 u38;
        public Int32 u39;
        public Int32 u40;
        public float RunSpeed2;
        public Int16 NPCWalkPos1;
        public Int16 NPCWalkPos2;
        public Int16 NPCWalkMode;
        public Int16 u41;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string mou4; //always this. assuming an animation name
        public UInt32 Status;
        public UInt32 StatusServer; //im assuming this is updated after the client asks the server. there is a noticable delay between the two
        public Int32 u42;
        public Int32 u43;
        public Int32 u44;
        public Int32 u45;
        public Int32 u46;
        public UInt32 ClaimID; // The ID of the last person to perform an action on the mob
        public Int32 u47;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation2;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation3;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation4;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation5;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation6;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation7;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation8;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Animation9;
        public Int16 AnimationTime; //guessed, but something to do with the current animation
        public Int16 AnimationStep; //guessed, but something to do with the current animation
        public Int16 u48;
        public Int16 u49;
        public UInt16 EmoteID;
        public Int16 u50;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string EmoteName;
        public byte SpawnType;
        public byte u51;
        public Int16 u52;
        public byte LSColorRed;
        public byte LSColorGreen;
        public byte LSColorBlue;
        public byte u53;
        public byte u54;
        public byte u55;
        public byte CampaignMode; //boolean value. 
        public byte u56;
        public byte FishingTimer;
        public Int32 u59;
        public Int32 u60;
        public Int32 u61;
        public Int32 u62;
        public Int32 u63;
        public Int32 u64;
        public Int16 u65;
        public UInt16 PetIndex;
        public Int32 u68;
        public Int32 u69;
        public float ModelSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 148)]
        public string u70;
        public UInt16 FellowIndex;
    }

    public enum FFXISpawnType : byte
    {
        Player = 1,
        NPC = 2,
        GroupMember = 4, //YOU the player and your group members set both group and ally.
        AllianceMember = 8, //YOUR alliance members not in your group set only this. 
        MOB = 16,//Also includes event npcs that can be healed (ie besieged/assault/missions)
        Door = 32 //could be more than just doors...
        //???             = 64
        //???             = 128
    }

    public enum FFXIModelType : byte
    {
        Player = 0,
        NPCRacial = 1,
        NPCUnique = 2,
        NPCObject = 3, //so far only doors...
        Boat = 5, //Airship and Ferries
        //                = 7  //i know this exists, but ive only seen it once.
    }

    public enum FFXIRenderFlags : byte
    {
        //???               1
        //???               2
        Viewable = 4,
        //???               8
        //???               16
        MOB = 32,
        Hidden = 64
        //???               128
    }

    public enum FFXISpawnFlags5 : byte
    {
        //???               1
        //???               2
        //???               4
        //???               8
        SeekingParty = 16,
        AutoGroup = 32, //the orange flag that no one uses
        AwayMode = 64,
        Anonymous = 128
    }

    public enum FFXISpawnFlags6 : byte
    {
        CallForHelp = 1, //name turns yellow
        //???               2
        OfficialStaff = 4, //playonline "swirl" icon
        LinkshellEquipped = 8,
        ConnectionLost = 16 //DC icon
        //???               32
        //???               64
        //???               128
    }

    public enum FFXISpawnFlags7 : byte
    {
        //???               1
        //???               2
        //???               4
        NameHidden = 8,  //used for objects (like furniture) that do not display a name
        Untargetable = 16, //You will not be able to target things with this flag
        //???               32
        InvisEffect = 64, //YOU the player is given the partial invis haze
        InvisEffectSVR = 128 //assuming server confirmed
    }

    public enum FFXISpawnFlags8 : byte
    {
        SneakEffect = 1,  //footstep sounds are silenced
        Bazaar = 2,
        //???           4
        TrialPlayer = 8,  //combined with GM flag   = SGM
        TrialPlayer2 = 16, //combined with GM flag   = LGM
        GM = 32  //combined with above two = OSM (sage sundi mode)
        //???           64
        //???           128
    }

    //WARNING: Some of these flags have shifted down by two bits since the july 2011 patch
    //         and are no longer accurate. Only FFXISpawnFlags14.Attackable has been changed here.
    public enum FFXISpawnFlags14 : byte
    {
        //???           1
        //???           2
        OpenSeason = 4,  //Set by besieged mobs. assuming this means its a free for all fight fest (claim disabled). need verification!
        Attackable = 32  //This flag is set when the mob is in combat mode and you or pt member has the claim, but not ally/besieged/dynamis
        //???           16
        //???           32
        //???           64
        //              128 //festival decorations are known to set this
    }

    public enum FFXISpawnFlags15 : byte
    {
        //???           1
        Flag2 = 2,  //Known setters: FOV crates, random hidden npc's, synergy furnaces, gargoyles in beac.s
        Furniture = 4   //moghouse furniture
        //???           8
        //???           16
        //???           32
        //???           64
        //???           128
    }

    public enum FFXISpawnFlags16 : byte
    {
        //???           1
        //???           2
        //???           4
        Mentor = 8,
        NewPlayer = 16 //has a '?' above thier head
        //???           32
        //???           64
        //???           128
    }

    public enum FFXISpawnFlags17 : byte
    {
        //???           1
        //???           2
        //???           4
        //???           8
        //???           16
        LevelSync = 32
        //???           64
        //???           128
    }

    public enum FFXICombatFlags : byte
    {
        InCombat = 1,  //100% confirmed: player pulls out weapon if set. WARNING: /heal /sit & some town NPCs sets this as well
        Dead = 2   //100% confirmed: things get the death animation if set. WARNING: /sit sets this as well
        //              4     /sit sets this
        //              8     /sit sets this. cant move if set.
        //              16    cant move if set
        //              32    /heal sets this. cant move if set.
        //              64    cant move if set
        //              128   cant move if set
        // this *IS* a bitfield: if you one-shot something the corpse will be 2 instead of 3 (died out of combat)
    }


    /// <summary>A FFXI-specific spawn that updates itself from game memory.</summary>
    public class FFXISpawn : GameSpawn
    {
        private IntPtr m_pointer;
        private MemoryReader m_reader;
        private FFXIGameInstance m_instance;
        private UInt32 m_serverID;
        private UInt32 m_serverClaimID = 0;

        public FFXISpawn(uint ID, IntPtr pointer, FFXIGameInstance instance)
        {
            base.ID = ID;
            m_pointer = pointer;
            m_instance = instance;
            m_reader = instance.Reader;
            Update(); //read the memory straight away so that the server id is available
        }

        /// <summary>The memory address to retrieve the current spawn state from.</summary>
        public IntPtr Pointer
        {
            get { return m_pointer; }
        }

        /// <summary>The server id assigned to this spawn. This is FFXI specific data.</summary>
        public uint ServerID
        {
            get { return m_serverID; }
        }

        /// <summary>Updates the spawn state from game memory.</summary>
        public override void Update()
        {
            try
            {
                //read the memory for the this spawn
                SpawnInfo info = m_reader.ReadStruct<SpawnInfo>(m_pointer);

                //The Zone ID is always known upfront in FFXI. This is a sanity check.
                if (info.ZoneID != base.ID)
                    return;

                m_serverID = info.ServerID;
                base.Location.X = info.X;
                base.Location.Y = info.Y;
                base.Location.Z = info.Z;
                base.Heading = info.Heading;
                base.HealthPercent = info.HealthPercent;
                base.Speed = info.RunSpeed;
                base.Hidden = false;
                base.Dead = false;
                base.InCombat = false;
                base.GroupMember = false;
                base.RaidMember = false;
                base.Name = info.DisplayName;
                base.Level = -1; //level is never known in FFXI
                base.Distance = 0;
                base.PetIndex = info.PetIndex;
                base.Attackable = true;
                base.FellowIndex = info.FellowIndex;

                //calculate the distance between the player and this spawn.
                if (m_instance.Engine.Game.Player != null && m_instance.Engine.Game.Player != this)
                    base.Distance = (float)m_instance.Engine.Game.Player.Location.calcDist2D(base.Location);

                //set linkshell color for players
                if (base.Type == SpawnType.Player)
                    base.FillColor = Color.FromArgb(info.LSColorRed, info.LSColorGreen, info.LSColorBlue);

                //set the spawn type
                if ((info.SpawnType & (int)FFXISpawnType.Player) != 0)
                {
                    base.Type = SpawnType.Player;
                }
                else if ((info.SpawnType & (int)FFXISpawnType.NPC) != 0)
                {
                    base.Type = SpawnType.NPC;
                }
                else if ((info.SpawnType & (int)FFXISpawnType.MOB) != 0)
                {
                    base.Type = SpawnType.MOB;
                }

                //only process the claim if the id has changed
                if (m_serverClaimID != info.ClaimID)
                {
                    if (info.ClaimID > 0)
                    {
                        //Claims use server IDs not zone IDs. Since the engine does not know what a server id is, a lookup must be done ahead of time
                        UInt32 lookup = m_instance.lookupZoneIDFromServerID(info.ClaimID);

                        //it is possible that the lookup will fail if the claimee is added after the claimed mob (can happen on zone).
                        if (lookup > 0)
                        {
                            base.ClaimID = lookup;
                            m_serverClaimID = info.ClaimID; //only lock things in if the lookup is successful.
                        }
                    }
                    else
                    {
                        //the mob is no longer claimed, so zero things out
                        base.ClaimID = 0;
                        m_serverClaimID = 0;
                    }
                }

                //set party/alliance flags
                if ((info.SpawnType & (int)FFXISpawnType.GroupMember) != 0)
                    base.GroupMember = true;
                if ((info.SpawnType & (int)FFXISpawnType.AllianceMember) != 0)
                    base.RaidMember = true;

                //set combat/visibility modes
                if ((info.Flags1 & (int)FFXIRenderFlags.Hidden) != 0)
                    base.Hidden = true;
                if ((info.Status & (int)FFXICombatFlags.Dead) != 0 && info.Status < 4) //prevent sit and heal messing with us
                    base.Dead = true;
                if ((info.Status & (int)FFXICombatFlags.InCombat) != 0 && info.Status < 4) //prevent sit and heal messing with us
                    base.InCombat = true;

                base.Attackable = base.Type == SpawnType.MOB && !base.Dead && info.ClaimID > 0 && (info.Flags5 & ((int)FFXISpawnFlags14.Attackable << 16)) != 0;

                //set icon if any of these situations are met
                if (base.Type == SpawnType.MOB && base.InCombat && !base.Dead)
                {
                    if (info.ClaimID > 0)
                    {
                        if ((info.Flags5 & ((int)FFXISpawnFlags14.Attackable << 16)) != 0)
                        {
                            base.Icon = MapRes.StatusBattleTarget;
                        }
                        else
                        {
                            base.Icon = MapRes.StatusClaimed;
                        }
                    }
                    else
                    {
                        base.Icon = MapRes.StatusAggro;
                    }
                }
                else if ((info.Flags6 & (int)FFXISpawnFlags6.ConnectionLost) != 0)
                {
                    base.Icon = MapRes.StatusDisconnected;
                }
                else if (base.Dead)
                {
                    base.Icon = MapRes.StatusDead;
                }
                else if (base.Alert)
                {
                    base.Icon = MapRes.StatusAlert;
                }
                else if ((info.Flags2 & (int)FFXISpawnFlags8.GM) != 0)
                {
                    base.Icon = MapRes.StatusGM;
                }
                else if (base.Type == SpawnType.MOB && info.CampaignMode > 0)
                {
                    base.Icon = MapRes.StatusCampaign;
                }
                else if ((info.Flags1 & ((int)FFXISpawnFlags7.InvisEffectSVR << 24)) != 0)
                {
                    base.Icon = MapRes.StatusInvisible;
                }
                else
                {
                    base.Icon = null;
                }
            }
            catch { }
        }
    }

    public interface IFFXIGameContainer
    {
        event EventHandler ZoneChanged;
        event EventHandler MapChanged;
    }

    public interface IFFXIMapImageContainer
    {
        FFXIImageMaps Maps { get; }
        FFXIImageMap CurrentMap { get; }
        FFXIZoneMaps CurrentZone { get; }
        void ResetImageMap();
        void Save();
    }

    /// <summary>A collection of image maps keyed by its zone</summary>
    public class FFXIImageMaps
    {
        private Dictionary<int, FFXIZoneMaps> m_zones;
        private string m_filePath;
        private string m_lastLoad;
        private string[] m_extentionList;

        public FFXIImageMaps(string ImageExtList, string FilePath)
        {
            m_extentionList = ImageExtList.Split('|');
            m_filePath = FilePath;
            m_lastLoad = "";
            m_zones = new Dictionary<int, FFXIZoneMaps>();
            LoadMapData();
        }

        /// <summary>Gets the file extention to be appended to the generated map file.</summary>
        public string[] FileExtList
        {
            get { return m_extentionList; }
        }

        /// <summary>Gets or sets the file path where the map pack is located at.</summary>
        public string FilePath
        {
            get { return m_filePath; }
            set
            {
                m_filePath = value;
                Reload();
            }
        }

        /// <summary>Gets whether and zone data has been loaded or not.</summary>
        public bool Empty
        {
            get { return m_zones.Count == 0; }
        }

        /// <summary>Determines if the given zone id exists within the collection.</summary>
        public bool ContainsKey(int key)
        {
            return m_zones.ContainsKey(key);
        }

        /// <summary>Gets the specified zone collection.</summary>
        public FFXIZoneMaps this[int key]
        {
            get { return m_zones[key]; }
        }

        /// <summary>Gets the currently applicable map.</summary>
        public FFXIImageMap GetCurrentMap(int ZoneID, MapPoint point)
        {
            if (m_zones.ContainsKey(ZoneID))
                return m_zones[ZoneID].GetCurrentMap(point);
            return null;
        }

        /// <summary>Reloads the map data.</summary>
        public void Reload()
        {
            m_zones.Clear();
            LoadMapData();
        }

        /// <summary>Releases cached image data across all zones.</summary>
        public void ClearCache()
        {
            foreach (KeyValuePair<int, FFXIZoneMaps> pair in m_zones)
                pair.Value.ClearCache();
        }

        /// <summary>Releases cached image data for the specified zone.</summary>
        public void ClearCache(int ZoneID)
        {
            if (m_zones.ContainsKey(ZoneID))
                m_zones[ZoneID].ClearCache();
        }


        /// <summary>Writes map data to the currently loaded file.</summary>
        public void SaveMapData()
        {
            if (m_lastLoad == "")
                return;
            SaveMapData(m_lastLoad);
        }

        /// <summary>Writes map data to the specified file.</summary>
        public void SaveMapData(string FilePath)
        {
            string tempFile = Path.GetTempFileName();
            string output;
            int zoneID;
            FFXIImageMap map;

            try
            {
                //Save the data to a temp file first, so that if an error occurs the ini file isnt left empty
                FileStream stream = File.Open(tempFile, FileMode.Create, FileAccess.Write);
                StreamWriter writer = new StreamWriter(stream);

                //hey it is an ini file afterall
                writer.WriteLine("[Map]");

                //sort zones by id
                SortedList<int, FFXIZoneMaps> zones = new SortedList<int, FFXIZoneMaps>(m_zones);
                foreach (KeyValuePair<int, FFXIZoneMaps> zonepair in zones)
                {
                    zoneID = zonepair.Value.ZoneID;

                    foreach (KeyValuePair<int, FFXIImageMap> mappair in zonepair.Value)
                    {
                        map = mappair.Value;

                        output = string.Format(CultureInfo.InvariantCulture, "{0:X2}_{1:#0}={2:0.###},{3:0.###},{4:0.###},{5:0.###}", zoneID, map.MapID, map.XScale, map.XOffset, map.YScale, map.YOffset);
                        foreach (FFXIImageMapRange range in map)
                        {
                            output += string.Format(CultureInfo.InvariantCulture, ",{0:0.###},{1:0.###},{2:0.###},{3:0.###},{4:0.###},{5:0.###}", range.Left, range.Floor, range.Top, range.Right, range.Ceiling, range.Bottom); //XZY ordering
                        }
                        writer.WriteLine(output);
                    }
                }
                writer.Close();

                //overwrite the old file with the new one
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tempFile, FilePath);
            }
            catch (Exception ex)
            {
                if (File.Exists(FilePath)) //if the temp file still exists, remove it
                    File.Delete(tempFile);
                throw ex; //escalate error to caller
            }
        }

        /// <summary>Loads and parses the map.ini from the current map path.</summary>
        private void LoadMapData()
        {
            if (m_filePath == "")
                return;

            if (!File.Exists(m_filePath + Program.MapIniFile))
            {
                Debug.WriteLine("WARNING: map ini does not exist");
                return;
            }

            try
            {
                //Why have the overhead of getprivateprofile when we can just do it ourself?
                m_lastLoad = m_filePath + Program.MapIniFile;
                FileStream stream = File.OpenRead(m_lastLoad);
                StreamReader reader = new StreamReader(stream);
                string line = "";
                string subline = "";
                string[] parts;
                string[] subparts;
                int zoneid;
                int mapid;
                int linenumber = 0;
                int idx = 0;
                FFXIZoneMaps zonemaps = null;
                FFXIImageMap map = null;

                while ((line = reader.ReadLine()) != null)
                {
                    linenumber++;
                    try
                    {
                        if (line.Length == 0 || line[0] == ';' || line[0] == '[') //only care about the data
                            continue;

                        //parse the key/value pair of the ini line
                        parts = line.Split('=');
                        if (parts.Length != 2)
                            continue;

                        //parse the zone/map pair
                        subparts = parts[0].Split('_');
                        if (subparts.Length != 2)
                            continue;

                        zoneid = System.Convert.ToInt16(subparts[0], 16);
                        if (!int.TryParse(subparts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out mapid))
                            continue;

                        //parse the calc and range data
                        subline = parts[1];
                        idx = subline.IndexOf(';');
                        if (idx > -1)
                            subline = subline.Substring(0, idx); //kill comments following the data area
                        subline = subline.Trim();
                        subparts = subline.Split(',');
                        if (subparts.Length == 0 || ((subparts.Length - 4) % 6 != 0)) //ranges are specified in vector pairs
                            continue;

                        //get the zone object
                        if (zonemaps == null || zonemaps.ZoneID != zoneid)
                        {
                            if (!m_zones.ContainsKey(zoneid))
                            {
                                zonemaps = new FFXIZoneMaps(this, zoneid);
                                m_zones.Add(zoneid, zonemaps);
                            }
                            else
                            {
                                zonemaps = m_zones[zoneid];
                            }
                        }

                        if (zonemaps.ContainsKey(mapid)) //only process the same zone/map combo once
                            continue;

                        //create the map object
                        map = new FFXIImageMap(zonemaps, mapid,
                           float.Parse(subparts[0], CultureInfo.InvariantCulture),
                           float.Parse(subparts[1], CultureInfo.InvariantCulture),
                           float.Parse(subparts[2], CultureInfo.InvariantCulture),
                           float.Parse(subparts[3], CultureInfo.InvariantCulture)
                        );

                        //add each range of values
                        int i = 4;
                        while (i < subparts.Length)
                        {
                            map.addRange(
                               float.Parse(subparts[i], CultureInfo.InvariantCulture),      //X1 
                               float.Parse(subparts[i + 2], CultureInfo.InvariantCulture),  //Y1 Y/Z swapped to standard vertex order.
                               float.Parse(subparts[i + 1], CultureInfo.InvariantCulture),  //Z1 Why must we perpetuate bad coordinate ordering?
                               float.Parse(subparts[i + 3], CultureInfo.InvariantCulture),  //X2 
                               float.Parse(subparts[i + 5], CultureInfo.InvariantCulture),  //Y2 Y/Z swapped here too.
                               float.Parse(subparts[i + 4], CultureInfo.InvariantCulture)   //Z2
                            );
                            i += 6;
                        }
                        zonemaps.Add(map);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("error reading line " + linenumber + ": " + ex.Message);
                    }
                }
                reader.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LoadMapData ERROR: " + ex.Message);
            }
        }
    }

    /// <summary>A collection of image maps that belong to a given zone.</summary>
    public class FFXIZoneMaps
    {
        FFXIImageMaps m_parent;
        Dictionary<int, FFXIImageMap> m_maps;
        int zoneid;

        /// <summary>Gets the zone id of this collection.</summary>
        public int ZoneID
        {
            get { return zoneid; }
        }

        /// <summary>Gets the parent map collection of this zone.</summary>
        public FFXIImageMaps Maps
        {
            get { return m_parent; }
        }

        public FFXIZoneMaps(FFXIImageMaps parent, int zoneid)
        {
            m_maps = new Dictionary<int, FFXIImageMap>();
            m_parent = parent;
            this.zoneid = zoneid;
        }

        /// <summary>Adds the image map to the zone.</summary>
        public void Add(FFXIImageMap map)
        {
            m_maps.Add(map.MapID, map);
        }

        /// <summary>Determines if the zone id is contained in the collection.</summary>
        public bool ContainsKey(int key)
        {
            return m_maps.ContainsKey(key);
        }

        /// <summary>Determines if the given map is bound to the zone.</summary>
        public bool ContainsValue(FFXIImageMap map)
        {
            return m_maps.ContainsValue(map);
        }

        public Dictionary<int, FFXIImageMap>.Enumerator GetEnumerator()
        {
            return m_maps.GetEnumerator();
        }

        /// <summary>Gets the specified map from the zone collection.</summary>
        public FFXIImageMap this[int key]
        {
            get { return m_maps[key]; }
        }

        /// <summary>Gets the currently applicable zone map.</summary>
        public FFXIImageMap GetCurrentMap(MapPoint point)
        {
            foreach (KeyValuePair<int, FFXIImageMap> pair in m_maps)
            {
                if (pair.Value.Contains(point))
                    return (pair.Value);
            }
            return null;
        }

        /// <summary>Gets a list of map id's registered in the zone</summary>
        public List<FFXIImageMap> MapList
        {
            get { return new List<FFXIImageMap>(m_maps.Values); }
        }

        /// <summary>Releases cached image data for the zone.</summary>
        public void ClearCache()
        {
            foreach (KeyValuePair<int, FFXIImageMap> pair in m_maps)
                pair.Value.ClearCache();
        }

        /// <summary>Creates a new map with the given id and scale</summary>
        public FFXIImageMap Create(int ID, float scale)
        {
            return Create(ID, scale, 0, 0);
        }
        /// <summary>Creates a new map with the given id, scale, and offset</summary>
        public FFXIImageMap Create(int ID, float scale, float x, float y)
        {
            if (m_maps.ContainsKey(ID))
                return m_maps[ID];
            FFXIImageMap map = new FFXIImageMap(this, ID, scale, x, -scale, y);
            Add(map);
            return map;
        }

        /// <summary>Remove the map with the given id from the zone collection.</summary>
        public void Remove(int ID)
        {
            if (m_maps.ContainsKey(ID))
                m_maps.Remove(ID);
        }

        /// <summary>Returns the next available map id not currently in use within the zone.</summary>
        public int GetFreeID()
        {
            List<int> list = new List<int>(m_maps.Keys);
            if (list.Count > 0)
            {
                list.Sort();
                int max = list[list.Count - 1];

                for (int i = 0; i <= max; i++)
                {
                    if (!m_maps.ContainsKey(i))
                        return i;
                }
                return max + 1;
            }
            return 0;
        }
    }

    /// <summary>Defines a single axis-aligned bounding box (AABB).</summary>
    public class FFXIImageMapRange
    {
        private float minX;
        private float maxX;
        private float minY;
        private float maxY;
        private float minZ;
        private float maxZ;
        private FFXIImageMap m_map;

        public FFXIImageMapRange(FFXIImageMap map, float x1, float y1, float z1, float x2, float y2, float z2)
        {
            //cache the mins/maxs
            m_map = map;
            SetRange(x1, y1, z1, x2, y2, z2);
        }

        public float Floor
        {
            get { return minZ; }
            set { minZ = value; }
        }
        public float Ceiling
        {
            get { return maxZ; }
            set { maxZ = value; }
        }
        public float Left
        {
            get { return minX; }
            set { minX = value; }
        }
        public float Top
        {
            get { return minY; }
            set { minY = value; }
        }
        public float Right
        {
            get { return maxX; }
            set { maxX = value; }
        }
        public float Bottom
        {
            get { return maxY; }
            set { maxY = value; }
        }
        public RectangleF Bounds
        {
            get { return RectangleF.FromLTRB(minX, minY, maxX, maxY); }
            set
            {
                minX = value.Left;
                minY = value.Top;
                maxX = value.Right;
                maxY = value.Bottom;
            }
        }

        public void SetRange(MapPoint p1, MapPoint p2)
        {
            SetRange(p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z);
        }
        public void SetRange(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            minX = Math.Min(x1, x2);
            maxX = Math.Max(x1, x2);
            minY = Math.Min(y1, y2);
            maxY = Math.Max(y1, y2);
            minZ = Math.Min(z1, z2);
            maxZ = Math.Max(z1, z2);
        }

        /// <summary>Check the 3D point against the AABB</summary>
        public bool Contains(MapPoint point)
        {
            return Contains(point.X, point.Y, point.Z);
        }
        /// <summary>Check the 3D point against the AABB</summary>
        public bool Contains(float x, float y, float z)
        {
            return x <= maxX && x >= minX &&
                   y <= maxY && y >= minY &&
                   z <= maxZ && z >= minZ;
        }

        /// <summary>Check the 2D point against the AABB</summary>
        public bool Contains(PointF point)
        {
            return Contains(point.X, point.Y);
        }
        /// <summary>Check the 2D point against the AABB</summary>
        public bool Contains(float x, float y)
        {
            return x <= maxX && x >= minX &&
                   y <= maxY && y >= minY;
        }
    }

    /// <summary>Defines information about a given image map that is displayed in the client as a vector alternative.</summary>
    public class FFXIImageMap
    {
        FFXIZoneMaps m_parent;
        int m_mapid;
        float m_xScale;
        float m_xOffset;
        float m_yScale;
        float m_yOffset;
        List<FFXIImageMapRange> ranges;
        Image mapImage;
        bool loadAttempted = false;
        RectangleF bounds = RectangleF.Empty;

        public FFXIImageMap(FFXIZoneMaps parent, int mapid, float xScale, float xOffset, float yScale, float yOffset)
        {
            m_parent = parent;
            ranges = new List<FFXIImageMapRange>();
            m_mapid = mapid;
            m_xScale = xScale;
            m_xOffset = xOffset;
            m_yScale = yScale;
            m_yOffset = yOffset;
        }

        /// <summary>Adds a detection range. Ranges are axis aligned bounding boxes (AABB) that define the applicability of a given map.</summary>
        public void addRange(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            FFXIImageMapRange range = new FFXIImageMapRange(this, x1, y1, z1, x2, y2, z2);
            ranges.Add(range);
        }
        /// <summary>Adds a detection range. Ranges are axis aligned bounding boxes (AABB) that define the applicability of a given map.</summary>
        public void addRange(MapPoint p1, MapPoint p2)
        {
            addRange(p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z);
        }

        /// <summary>Determines whether any of the defined ranges are applicable to the given MAP coordinate.</summary>
        public bool Contains(MapPoint point)
        {
            foreach (FFXIImageMapRange range in ranges)
            {
                if (range.Contains(point))
                    return true;
            }
            return false;
        }

        /// <summary>Gets the X zone scale.</summary>
        public float XScale
        {
            get { return m_xScale; }
        }
        /// <summary>Gets the X offset to place map from zone center.</summary>
        public float XOffset
        {
            get { return m_xOffset; }
        }
        /// <summary>Gets the Y zone scale.</summary>
        public float YScale
        {
            get { return m_yScale; }
        }
        /// <summary>Gets the Y offset to place map from zone center.</summary>
        public float YOffset
        {
            get { return m_yOffset; }
        }

        public List<FFXIImageMapRange>.Enumerator GetEnumerator()
        {
            return ranges.GetEnumerator();
        }

        public FFXIImageMapRange this[int index]
        {
            get { return ranges[index]; }
        }
        public int Count
        {
            get { return ranges.Count; }
        }

        public void SetMapLocation(float X, float Y)
        {
            m_xOffset = X;
            m_yOffset = Y;
            bounds = RectangleF.Empty; //force recalculation
        }
        public void SetMapLocation(float scale, float X, float Y)
        {
            SetMapLocation(scale, -scale, X, Y);
        }
        public void SetMapLocation(float Xscale, float Yscale, float X, float Y)
        {
            m_xScale = Xscale;
            m_yScale = Yscale;
            SetMapLocation(X, Y);
        }

        /// <summary>Gets the map id of this map image.</summary>
        public int MapID
        {
            get { return m_mapid; }
        }

        /// <summary>Translate a MAP coordinate into an IMAGE coordinate.</summary>
        public PointF Translate(MapPoint point)
        {
            return new PointF(
               (m_xOffset + (m_xScale * point.X)),
               (m_yOffset + (m_yScale * point.Y))
            );
        }

        /// <summary>Translate an IMAGE coordinate into a MAP coordinate.</summary>
        public MapPoint Translate(PointF point)
        {
            return new MapPoint(
               ((point.X - m_xOffset) / m_xScale),
               ((point.Y - m_yOffset) / m_yScale),
               0
            );
        }

        /// <summary>Retrieve the image boundaries in MAP coordinates.</summary>
        public RectangleF Bounds
        {
            get
            {
                //The bounds will never change, so only calculate it once (upon demand) and then cache it
                if (bounds == RectangleF.Empty)
                {
                    bounds = new RectangleF(
                       (-m_xOffset / m_xScale),
                       (-m_yOffset / m_yScale),
                       (512 / m_xScale) * 0.5f, //map is scaled by a factor of 2, so reduce the value by half
                       (512 / m_yScale) * 0.5f
                    );
                }
                return bounds;
            }
        }

        /// <summary>Gets the zone collection this map is part of.</summary>
        public FFXIZoneMaps Zone
        {
            get { return m_parent; }
        }

        /// <summary>Releases cached image data for the map.</summary>
        public void ClearCache()
        {
            mapImage = null;
            loadAttempted = false;
        }

        /// <summary>Loads and retrieves the map image.</summary>
        public Image GetImage()
        {
            if (mapImage == null && !loadAttempted)
            {
                try
                {
                    //search the map path for the zone image, using the list of supported extentions. take the first one found.
                    Bitmap tempImage = null;
                    for (int i = 0; i < m_parent.Maps.FileExtList.Length; i++)
                    {
                        //load the image file
                        string fullfilepath = m_parent.Maps.FilePath + m_parent.ZoneID.ToString("X2") + "_" + m_mapid + m_parent.Maps.FileExtList[i];
                        if (File.Exists(fullfilepath))
                        {
                            Debug.WriteLine("GetImage: loading " + fullfilepath);
                            tempImage = new Bitmap(fullfilepath);
                            break;
                        }
                    }
                    if (tempImage != null)
                    {
                        //if the image is not already at 32 alpha, then convert it. This fixes alpha issues when running on win 7
                        if (tempImage.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                        {
                            Bitmap newImage = new Bitmap(tempImage.Width, tempImage.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            newImage.SetResolution(tempImage.HorizontalResolution, tempImage.VerticalResolution);
                            Graphics g = Graphics.FromImage(newImage);
                            g.DrawImage(tempImage, 0, 0);
                            g.Dispose();
                            mapImage = newImage;
                        }
                        else
                        {
                            mapImage = tempImage;
                        }
                    }
                    else
                    {
                        Debug.WriteLine("GetImage: no suitable image could be found for zone " + m_parent.ZoneID.ToString("X2"));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("GetImage ERROR: " + ex.Message);
                }
                //only attempt to load once in case the image file is missing.
                loadAttempted = true;
            }
            return mapImage;
        }

        public override string ToString()
        {
            return m_mapid.ToString();
        }
    }
}