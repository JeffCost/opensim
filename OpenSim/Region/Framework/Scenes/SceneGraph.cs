/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Threading;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.Packets;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes.Types;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.Framework.Scenes
{
    public delegate void PhysicsCrash();

    public delegate void ObjectDuplicateDelegate(EntityBase original, EntityBase clone);

    public delegate void ObjectCreateDelegate(EntityBase obj);

    public delegate void ObjectDeleteDelegate(EntityBase obj);

    //SYMMETRIC SYNC
    public delegate void ObjectCreateBySyncDelegate(EntityBase obj);
    //end of SYMMETRIC SYNC


    /// <summary>
    /// This class used to be called InnerScene and may not yet truly be a SceneGraph.  The non scene graph components
    /// should be migrated out over time.
    /// </summary>
    public class SceneGraph
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region Events

        protected internal event PhysicsCrash UnRecoverableError;
        private PhysicsCrash handlerPhysicsCrash = null;

        public event ObjectDuplicateDelegate OnObjectDuplicate;
        public event ObjectCreateDelegate OnObjectCreate;
        public event ObjectDeleteDelegate OnObjectRemove;

        //SYMMETRIC SYNC
        public event ObjectCreateBySyncDelegate OnObjectCreateBySync;
        //end of SYMMETRIC SYNC

        #endregion

        #region Fields

        protected object m_presenceLock = new object();
        protected Dictionary<UUID, ScenePresence> m_scenePresenceMap = new Dictionary<UUID, ScenePresence>();
        protected List<ScenePresence> m_scenePresenceArray = new List<ScenePresence>();

        protected internal EntityManager Entities = new EntityManager();

        protected RegionInfo m_regInfo;
        protected Scene m_parentScene;
        protected Dictionary<UUID, SceneObjectGroup> m_updateList = new Dictionary<UUID, SceneObjectGroup>();
        protected int m_numRootAgents = 0;
        protected int m_numPrim = 0;
        protected int m_numChildAgents = 0;
        protected int m_physicalPrim = 0;

        protected int m_activeScripts = 0;
        protected int m_scriptLPS = 0;

        protected internal object m_syncRoot = new object();

        protected internal PhysicsScene _PhyScene;

        protected internal Dictionary<uint, SceneObjectGroup> SceneObjectGroupsByLocalID = new Dictionary<uint, SceneObjectGroup>();
        protected internal Dictionary<UUID, SceneObjectGroup> SceneObjectGroupsByFullID = new Dictionary<UUID, SceneObjectGroup>();

        private Object m_updateLock = new Object();

        #endregion

        protected internal SceneGraph(Scene parent, RegionInfo regInfo)
        {
            m_parentScene = parent;
            m_regInfo = regInfo;
        }

        public PhysicsScene PhysicsScene
        {
            get { return _PhyScene; }
            set
            {
                // If we're not doing the initial set
                // Then we've got to remove the previous
                // event handler

                if (_PhyScene != null)
                    _PhyScene.OnPhysicsCrash -= physicsBasedCrash;

                _PhyScene = value;

                if (_PhyScene != null)
                    _PhyScene.OnPhysicsCrash += physicsBasedCrash;
            }
        }

        protected internal void Close()
        {
            lock (m_presenceLock)
            {
                Dictionary<UUID, ScenePresence> newmap = new Dictionary<UUID, ScenePresence>();
                List<ScenePresence> newlist = new List<ScenePresence>();
                m_scenePresenceMap = newmap;
                m_scenePresenceArray = newlist;
            }

            lock (SceneObjectGroupsByFullID)
                SceneObjectGroupsByFullID.Clear();
            lock (SceneObjectGroupsByLocalID)
                SceneObjectGroupsByLocalID.Clear();

            Entities.Clear();
        }

        #region Update Methods

        protected internal void UpdatePreparePhysics()
        {
            // If we are using a threaded physics engine
            // grab the latest scene from the engine before
            // trying to process it.

            // PhysX does this (runs in the background).

            if (_PhyScene.IsThreaded)
            {
                _PhyScene.GetResults();
            }
        }

        protected internal void UpdatePresences()
        {
            ForEachScenePresence(delegate(ScenePresence presence)
            {
                presence.Update();
            });
        }

        protected internal float UpdatePhysics(double elapsed)
        {
            lock (m_syncRoot)
            {
                // Here is where the Scene calls the PhysicsScene. This is a one-way
                // interaction; the PhysicsScene cannot access the calling Scene directly.
                // But with joints, we want a PhysicsActor to be able to influence a
                // non-physics SceneObjectPart. In particular, a PhysicsActor that is connected
                // with a joint should be able to move the SceneObjectPart which is the visual
                // representation of that joint (for editing and serialization purposes).
                // However the PhysicsActor normally cannot directly influence anything outside
                // of the PhysicsScene, and the non-physical SceneObjectPart which represents
                // the joint in the Scene does not exist in the PhysicsScene.
                //
                // To solve this, we have an event in the PhysicsScene that is fired when a joint
                // has changed position (because one of its associated PhysicsActors has changed 
                // position).
                //
                // Therefore, JointMoved and JointDeactivated events will be fired as a result of the following Simulate().

                return _PhyScene.Simulate((float)elapsed);
            }
        }

        protected internal void UpdateScenePresenceMovement()
        {
            ForEachScenePresence(delegate(ScenePresence presence)
            {
                presence.UpdateMovement();
            });
        }

        public void GetCoarseLocations(out List<Vector3> coarseLocations, out List<UUID> avatarUUIDs, uint maxLocations)
        {
            coarseLocations = new List<Vector3>();
            avatarUUIDs = new List<UUID>();

            List<ScenePresence> presences = GetScenePresences();
            for (int i = 0; i < Math.Min(presences.Count, maxLocations); ++i)
            {
                ScenePresence sp = presences[i];
                
                // If this presence is a child agent, we don't want its coarse locations
                if (sp.IsChildAgent)
                    continue;

                if (sp.ParentID != 0)
                {
                    // sitting avatar
                    SceneObjectPart sop = m_parentScene.GetSceneObjectPart(sp.ParentID);
                    if (sop != null)
                    {
                        coarseLocations.Add(sop.AbsolutePosition + sp.OffsetPosition);
                        avatarUUIDs.Add(sp.UUID);
                    }
                    else
                    {
                        // we can't find the parent..  ! arg!
                        coarseLocations.Add(sp.AbsolutePosition);
                        avatarUUIDs.Add(sp.UUID);
                    }
                }
                else
                {
                    coarseLocations.Add(sp.AbsolutePosition);
                    avatarUUIDs.Add(sp.UUID);
                }
            }
        }

        #endregion

        #region Entity Methods

        /// <summary>
        /// Add an object into the scene that has come from storage
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, changes to the object will be reflected in its persisted data
        /// If false, the persisted data will not be changed even if the object in the scene is changed
        /// </param>
        /// <param name="alreadyPersisted">
        /// If true, we won't persist this object until it changes
        /// If false, we'll persist this object immediately
        /// </param>
        /// <param name="sendClientUpdates">
        /// If true, we send updates to the client to tell it about this object
        /// If false, we leave it up to the caller to do this
        /// </param>
        /// <returns>
        /// true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        protected internal bool AddRestoredSceneObject(
            SceneObjectGroup sceneObject, bool attachToBackup, bool alreadyPersisted, bool sendClientUpdates)
        {
            if (attachToBackup && (!alreadyPersisted))
            {
                sceneObject.ForceInventoryPersistence();
                sceneObject.HasGroupChanged = true;
            }

            return AddSceneObject(sceneObject, attachToBackup, sendClientUpdates);
        }
                
        /// <summary>
        /// Add a newly created object to the scene.  This will both update the scene, and send information about the
        /// new object to all clients interested in the scene.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, the object is made persistent into the scene.
        /// If false, the object will not persist over server restarts
        /// </param>
        /// <returns>
        /// true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        protected internal bool AddNewSceneObject(SceneObjectGroup sceneObject, bool attachToBackup, bool sendClientUpdates)
        {
            // Ensure that we persist this new scene object if it's not an
            // attachment
            if (attachToBackup)
                sceneObject.HasGroupChanged = true;

            return AddSceneObject(sceneObject, attachToBackup, sendClientUpdates);
        }
        
        /// <summary>
        /// Add a newly created object to the scene.
        /// </summary>
        /// 
        /// This method does not send updates to the client - callers need to handle this themselves.
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup"></param>
        /// <param name="pos">Position of the object</param>
        /// <param name="rot">Rotation of the object</param>
        /// <param name="vel">Velocity of the object.  This parameter only has an effect if the object is physical</param>
        /// <returns></returns>
        public bool AddNewSceneObject(
            SceneObjectGroup sceneObject, bool attachToBackup, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            AddNewSceneObject(sceneObject, true, false);

            // we set it's position in world.
            sceneObject.AbsolutePosition = pos;

            if (sceneObject.RootPart.Shape.PCode == (byte)PCode.Prim)
            {
                sceneObject.ClearPartAttachmentData();
            }
            
            sceneObject.UpdateGroupRotationR(rot);
            
            //group.ApplyPhysics(m_physicalPrim);
            if (sceneObject.RootPart.PhysActor != null && sceneObject.RootPart.PhysActor.IsPhysical && vel != Vector3.Zero)
            {
                sceneObject.RootPart.ApplyImpulse((vel * sceneObject.GetMass()), false);
                sceneObject.Velocity = vel;
            }
        
            return true;
        }

        /// <summary>
        /// Add an object to the scene.  This will both update the scene, and send information about the
        /// new object to all clients interested in the scene.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, the object is made persistent into the scene.
        /// If false, the object will not persist over server restarts
        /// </param>
        /// <param name="sendClientUpdates">
        /// If true, updates for the new scene object are sent to all viewers in range.
        /// If false, it is left to the caller to schedule the update
        /// </param>
        /// <returns>
        /// true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        protected bool AddSceneObject(SceneObjectGroup sceneObject, bool attachToBackup, bool sendClientUpdates)
        {
            if (sceneObject == null || sceneObject.RootPart == null || sceneObject.RootPart.UUID == UUID.Zero)
                return false;

            if (Entities.ContainsKey(sceneObject.UUID))
                return false;

            SceneObjectPart[] children = sceneObject.Parts;

            // Clamp child prim sizes and add child prims to the m_numPrim count
            if (m_parentScene.m_clampPrimSize)
            {
                foreach (SceneObjectPart part in children)
                {
                    Vector3 scale = part.Shape.Scale;

                    if (scale.X > m_parentScene.m_maxNonphys)
                        scale.X = m_parentScene.m_maxNonphys;
                    if (scale.Y > m_parentScene.m_maxNonphys)
                        scale.Y = m_parentScene.m_maxNonphys;
                    if (scale.Z > m_parentScene.m_maxNonphys)
                        scale.Z = m_parentScene.m_maxNonphys;

                    //part.Shape.Scale = scale;
                    part.Scale = scale;
                }
            }
            m_numPrim += children.Length;

            sceneObject.AttachToScene(m_parentScene);

            if (sendClientUpdates)
                //sceneObject.ScheduleGroupForFullUpdate();
                sceneObject.ScheduleGroupForFullUpdate(new List<SceneObjectPartProperties>(){SceneObjectPartProperties.None}); 
            
            Entities.Add(sceneObject);

            if (attachToBackup)
                sceneObject.AttachToBackup();
            
            if (OnObjectCreate != null)
                OnObjectCreate(sceneObject);

            lock (SceneObjectGroupsByFullID)
            {
                SceneObjectGroupsByFullID[sceneObject.UUID] = sceneObject;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByFullID[part.UUID] = sceneObject;
            }

            lock (SceneObjectGroupsByLocalID)
            {
                SceneObjectGroupsByLocalID[sceneObject.LocalId] = sceneObject;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByLocalID[part.LocalId] = sceneObject;
            }

            return true;
        }

        /// <summary>
        /// Delete an object from the scene
        /// </summary>
        /// <returns>true if the object was deleted, false if there was no object to delete</returns>
        public bool DeleteSceneObject(UUID uuid, bool resultOfObjectLinked)
        {
            EntityBase entity;
            if (!Entities.TryGetValue(uuid, out entity) || (!(entity is SceneObjectGroup)))
                return false;

            SceneObjectGroup grp = (SceneObjectGroup)entity;

            if (entity == null)
                return false;

            if (!resultOfObjectLinked)
            {
                m_numPrim -= grp.PrimCount;

                if ((grp.RootPart.Flags & PrimFlags.Physics) == PrimFlags.Physics)
                    RemovePhysicalPrim(grp.PrimCount);
            }

            if (OnObjectRemove != null)
                OnObjectRemove(Entities[uuid]);

            lock (SceneObjectGroupsByFullID)
            {
                SceneObjectPart[] parts = grp.Parts;
                for (int i = 0; i < parts.Length; i++)
                    SceneObjectGroupsByFullID.Remove(parts[i].UUID);
                SceneObjectGroupsByFullID.Remove(grp.RootPart.UUID);
            }

            lock (SceneObjectGroupsByLocalID)
            {
                SceneObjectPart[] parts = grp.Parts;
                for (int i = 0; i < parts.Length; i++)
                    SceneObjectGroupsByLocalID.Remove(parts[i].LocalId);
                SceneObjectGroupsByLocalID.Remove(grp.RootPart.LocalId);
            }

            return Entities.Remove(uuid);
        }

        /// <summary>
        /// Add an object to the list of prims to process on the next update
        /// </summary>
        /// <param name="obj">
        /// A <see cref="SceneObjectGroup"/>
        /// </param>
        protected internal void AddToUpdateList(SceneObjectGroup obj)
        {
            lock (m_updateList)
            {
                m_updateList[obj.UUID] = obj;
                //m_log.Debug("added " + obj.UUID + " to m_updateList");
            }
        }

        /// <summary>
        /// Process all pending updates
        /// </summary>
        protected internal void UpdateObjectGroups()
        {
            if (!Monitor.TryEnter(m_updateLock))
                return;
            try
            {
                List<SceneObjectGroup> updates;

                // Some updates add more updates to the updateList. 
                // Get the current list of updates and clear the list before iterating
                lock (m_updateList)
                {
                    updates = new List<SceneObjectGroup>(m_updateList.Values);

                    /*
                    if (updates.Count > 0)
                    {
                        m_log.Debug("SceneGraph: " + updates.Count + " objects to send updates for");
                    }
                     * */ 


                    m_updateList.Clear();
                }

                // Go through all updates
                for (int i = 0; i < updates.Count; i++)
                {
                    SceneObjectGroup sog = updates[i];

                    // Don't abort the whole update if one entity happens to give us an exception.
                    try
                    {
                        sog.Update();
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[INNER SCENE]: Failed to update {0}, {1} - {2}", sog.Name, sog.UUID, e);
                    }
                }
            }
            finally
            {
                Monitor.Exit(m_updateLock);
            }
        }

        protected internal void AddPhysicalPrim(int number)
        {
            m_physicalPrim++;
        }

        protected internal void RemovePhysicalPrim(int number)
        {
            m_physicalPrim--;
        }

        protected internal void AddToScriptLPS(int number)
        {
            m_scriptLPS += number;
        }

        protected internal void AddActiveScripts(int number)
        {
            m_activeScripts += number;
        }

        public void DropObject(uint objectLocalID, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group != null)
                m_parentScene.AttachmentsModule.DetachSingleAttachmentToGround(group.UUID, remoteClient);
        }

        protected internal void HandleUndo(IClientAPI remoteClient, UUID primId)
        {
            if (primId != UUID.Zero)
            {
                SceneObjectPart part =  m_parentScene.GetSceneObjectPart(primId);
                if (part != null)
                    part.Undo();
            }
        }
        protected internal void HandleRedo(IClientAPI remoteClient, UUID primId)
        {
            if (primId != UUID.Zero)
            {
                SceneObjectPart part = m_parentScene.GetSceneObjectPart(primId);
                if (part != null)
                    part.Redo();
            }
        }

        protected internal void HandleObjectGroupUpdate(
            IClientAPI remoteClient, UUID GroupID, uint objectLocalID, UUID Garbage)
        {
            if (!remoteClient.IsGroupMember(GroupID))
                return;

            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group != null)
            {
                if (group.OwnerID == remoteClient.AgentId)
                    group.SetGroup(GroupID, remoteClient);
            }
        }

        protected internal ScenePresence CreateAndAddChildScenePresence(IClientAPI client, AvatarAppearance appearance)
        {
            ScenePresence newAvatar = null;

            // ScenePresence always defaults to child agent
            newAvatar = new ScenePresence(client, m_parentScene, m_regInfo, appearance);

            AddScenePresence(newAvatar);

            return newAvatar;
        }

        /// <summary>
        /// Add a presence to the scene
        /// </summary>
        /// <param name="presence"></param>
        protected internal void AddScenePresence(ScenePresence presence)
        {
            // Always a child when added to the scene
            bool child = presence.IsChildAgent;

            if (child)
            {
                m_numChildAgents++;
            }
            else
            {
                m_numRootAgents++;
                presence.AddToPhysicalScene(false);
            }

            Entities[presence.UUID] = presence;

            lock (m_presenceLock)
            {
                Dictionary<UUID, ScenePresence> newmap = new Dictionary<UUID, ScenePresence>(m_scenePresenceMap);
                List<ScenePresence> newlist = new List<ScenePresence>(m_scenePresenceArray);

                if (!newmap.ContainsKey(presence.UUID))
                {
                    newmap.Add(presence.UUID, presence);
                    newlist.Add(presence);
                }
                else
                {
                    // Remember the old presene reference from the dictionary
                    ScenePresence oldref = newmap[presence.UUID];
                    // Replace the presence reference in the dictionary with the new value
                    newmap[presence.UUID] = presence;
                    // Find the index in the list where the old ref was stored and update the reference
                    newlist[newlist.IndexOf(oldref)] = presence;
                }

                // Swap out the dictionary and list with new references
                m_scenePresenceMap = newmap;
                m_scenePresenceArray = newlist;
            }
        }

        /// <summary>
        /// Remove a presence from the scene
        /// </summary>
        protected internal void RemoveScenePresence(UUID agentID)
        {
            if (!Entities.Remove(agentID))
            {
                m_log.WarnFormat(
                    "[SCENE]: Tried to remove non-existent scene presence with agent ID {0} from scene Entities list",
                    agentID);
            }

            lock (m_presenceLock)
            {
                Dictionary<UUID, ScenePresence> newmap = new Dictionary<UUID, ScenePresence>(m_scenePresenceMap);
                List<ScenePresence> newlist = new List<ScenePresence>(m_scenePresenceArray);

                // Remove the presence reference from the dictionary
                if (newmap.ContainsKey(agentID))
                {
                    ScenePresence oldref = newmap[agentID];
                    newmap.Remove(agentID);

                    // Find the index in the list where the old ref was stored and remove the reference
                    newlist.RemoveAt(newlist.IndexOf(oldref));
                    // Swap out the dictionary and list with new references
                    m_scenePresenceMap = newmap;
                    m_scenePresenceArray = newlist;
                }
                else
                {
                    m_log.WarnFormat("[SCENE]: Tried to remove non-existent scene presence with agent ID {0} from scene ScenePresences list", agentID);
                }
            }
        }

        protected internal void SwapRootChildAgent(bool direction_RC_CR_T_F)
        {
            if (direction_RC_CR_T_F)
            {
                m_numRootAgents--;
                m_numChildAgents++;
            }
            else
            {
                m_numChildAgents--;
                m_numRootAgents++;
            }
        }

        public void removeUserCount(bool TypeRCTF)
        {
            if (TypeRCTF)
            {
                m_numRootAgents--;
            }
            else
            {
                m_numChildAgents--;
            }
        }

        public void RecalculateStats()
        {
            int rootcount = 0;
            int childcount = 0;

            ForEachScenePresence(delegate(ScenePresence presence)
            {
                if (presence.IsChildAgent)
                    ++childcount;
                else
                    ++rootcount;
            });

            m_numRootAgents = rootcount;
            m_numChildAgents = childcount;
        }

        public int GetChildAgentCount()
        {
            // some network situations come in where child agents get closed twice.
            if (m_numChildAgents < 0)
            {
                m_numChildAgents = 0;
            }

            return m_numChildAgents;
        }

        public int GetRootAgentCount()
        {
            return m_numRootAgents;
        }

        public int GetTotalObjectsCount()
        {
            return m_numPrim;
        }

        public int GetActiveObjectsCount()
        {
            return m_physicalPrim;
        }

        public int GetActiveScriptsCount()
        {
            return m_activeScripts;
        }

        public int GetScriptLPS()
        {
            int returnval = m_scriptLPS;
            m_scriptLPS = 0;
            return returnval;
        }

        #endregion

        #region Get Methods
        /// <summary>
        /// Get the controlling client for the given avatar, if there is one.
        ///
        /// FIXME: The only user of the method right now is Caps.cs, in order to resolve a client API since it can't
        /// use the ScenePresence.  This could be better solved in a number of ways - we could establish an
        /// OpenSim.Framework.IScenePresence, or move the caps code into a region package (which might be the more
        /// suitable solution).
        /// </summary>
        /// <param name="agentId"></param>
        /// <returns>null if either the avatar wasn't in the scene, or
        /// they do not have a controlling client</returns>
        /// <remarks>this used to be protected internal, but that
        /// prevents CapabilitiesModule from accessing it</remarks>
        public IClientAPI GetControllingClient(UUID agentId)
        {
            ScenePresence presence = GetScenePresence(agentId);

            if (presence != null)
            {
                return presence.ControllingClient;
            }

            return null;
        }

        /// <summary>
        /// Get a reference to the scene presence list. Changes to the list will be done in a copy
        /// There is no guarantee that presences will remain in the scene after the list is returned.
        /// This list should remain private to SceneGraph. Callers wishing to iterate should instead
        /// pass a delegate to ForEachScenePresence.
        /// </summary>
        /// <returns></returns>
        private List<ScenePresence> GetScenePresences()
        {
            return m_scenePresenceArray;
        }

        /// <summary>
        /// Request a scene presence by UUID. Fast, indexed lookup.
        /// </summary>
        /// <param name="agentID"></param>
        /// <returns>null if the presence was not found</returns>
        protected internal ScenePresence GetScenePresence(UUID agentID)
        {
            Dictionary<UUID, ScenePresence> presences = m_scenePresenceMap;
            ScenePresence presence;
            presences.TryGetValue(agentID, out presence);
            return presence;
        }

        /// <summary>
        /// Request the scene presence by name.
        /// </summary>
        /// <param name="firstName"></param>
        /// <param name="lastName"></param>
        /// <returns>null if the presence was not found</returns>
        protected internal ScenePresence GetScenePresence(string firstName, string lastName)
        {
            List<ScenePresence> presences = GetScenePresences();
            foreach (ScenePresence presence in presences)
            {
                if (presence.Firstname == firstName && presence.Lastname == lastName)
                    return presence;
            }
            return null;
        }

        /// <summary>
        /// Request the scene presence by localID.
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if the presence was not found</returns>
        protected internal ScenePresence GetScenePresence(uint localID)
        {
            List<ScenePresence> presences = GetScenePresences();
            foreach (ScenePresence presence in presences)
                if (presence.LocalId == localID)
                    return presence;
            return null;
        }

        protected internal bool TryGetScenePresence(UUID agentID, out ScenePresence avatar)
        {
            Dictionary<UUID, ScenePresence> presences = m_scenePresenceMap;
            presences.TryGetValue(agentID, out avatar);
            return (avatar != null);
        }

        protected internal bool TryGetAvatarByName(string name, out ScenePresence avatar)
        {
            avatar = null;
            foreach (ScenePresence presence in GetScenePresences())
            {
                if (String.Compare(name, presence.ControllingClient.Name, true) == 0)
                {
                    avatar = presence;
                    break;
                }
            }
            return (avatar != null);
        }

        /// <summary>
        /// Get a scene object group that contains the prim with the given local id
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if no scene object group containing that prim is found</returns>
        public SceneObjectGroup GetGroupByPrim(uint localID)
        {
            EntityBase entity;
            if (Entities.TryGetValue(localID, out entity))
                return entity as SceneObjectGroup;

            //m_log.DebugFormat("Entered GetGroupByPrim with localID {0}", localID);
            SceneObjectGroup sog;
            lock (SceneObjectGroupsByLocalID)
                SceneObjectGroupsByLocalID.TryGetValue(localID, out sog);

            if (sog != null)
            {
                if (sog.HasChildPrim(localID))
                    return sog;
                SceneObjectGroupsByLocalID.Remove(localID);
            }

            EntityBase[] entityList = GetEntities();
            foreach (EntityBase ent in entityList)
            {
                //m_log.DebugFormat("Looking at entity {0}", ent.UUID);
                if (ent is SceneObjectGroup)
                {
                    sog = (SceneObjectGroup)ent;
                    if (sog.HasChildPrim(localID))
                    {
                        lock (SceneObjectGroupsByLocalID)
                            SceneObjectGroupsByLocalID[localID] = sog;
                        return sog;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Get a scene object group that contains the prim with the given uuid
        /// </summary>
        /// <param name="fullID"></param>
        /// <returns>null if no scene object group containing that prim is found</returns>
        public SceneObjectGroup GetGroupByPrim(UUID fullID)
        {
            SceneObjectGroup sog;
            lock (SceneObjectGroupsByFullID)
                SceneObjectGroupsByFullID.TryGetValue(fullID, out sog);

            if (sog != null)
            {
                if (sog.ContainsPart(fullID))
                    return sog;

                lock (SceneObjectGroupsByFullID)
                    SceneObjectGroupsByFullID.Remove(fullID);
            }

            EntityBase[] entityList = GetEntities();
            foreach (EntityBase ent in entityList)
            {
                if (ent is SceneObjectGroup)
                {
                    sog = (SceneObjectGroup)ent;
                    if (sog.HasChildPrim(fullID))
                    {
                        lock (SceneObjectGroupsByFullID)
                            SceneObjectGroupsByFullID[fullID] = sog;
                        return sog;
                    }
                }
            }

            return null;
        }

        protected internal EntityIntersection GetClosestIntersectingPrim(Ray hray, bool frontFacesOnly, bool faceCenters)
        {
            // Primitive Ray Tracing
            float closestDistance = 280f;
            EntityIntersection result = new EntityIntersection();
            EntityBase[] EntityList = GetEntities();
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectGroup reportingG = (SceneObjectGroup)ent;
                    EntityIntersection inter = reportingG.TestIntersection(hray, frontFacesOnly, faceCenters);
                    if (inter.HitTF && inter.distance < closestDistance)
                    {
                        closestDistance = inter.distance;
                        result = inter;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Get a part contained in this scene.
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(uint localID)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group == null)
                return null;
            return group.GetChildPart(localID);
        }
        
        /// <summary>
        /// Get a named prim contained in this scene (will return the first 
        /// found, if there are more than one prim with the same name)
        /// </summary>
        /// <param name="name"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(string name)
        {
            SceneObjectPart sop = null;

            Entities.Find(
                delegate(EntityBase entity)
                {
                    if (entity is SceneObjectGroup)
                    {
                        foreach (SceneObjectPart p in ((SceneObjectGroup)entity).Parts)
                        {
                            if (p.Name == name)
                            {
                                sop = p;
                                return true;
                            }
                        }
                    }

                    return false;
                }
            );

            return sop;
        }

        /// <summary>
        /// Get a part contained in this scene.
        /// </summary>
        /// <param name="fullID"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(UUID fullID)
        {
            SceneObjectGroup group = GetGroupByPrim(fullID);
            if (group == null)
                return null;
            return group.GetChildPart(fullID);
        }

        /// <summary>
        /// Returns a list of the entities in the scene.  This is a new list so no locking is required to iterate over
        /// it
        /// </summary>
        /// <returns></returns>
        protected internal EntityBase[] GetEntities()
        {
            return Entities.GetEntities();
        }

        public Dictionary<uint, float> GetTopScripts()
        {
            Dictionary<uint, float> topScripts = new Dictionary<uint, float>();

            EntityBase[] EntityList = GetEntities();
            int limit = 0;
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectGroup grp = (SceneObjectGroup)ent;
                    if ((grp.RootPart.GetEffectiveObjectFlags() & (uint)PrimFlags.Scripted) != 0)
                    {
                        if (grp.scriptScore >= 0.01)
                        {
                            topScripts.Add(grp.LocalId, grp.scriptScore);
                            limit++;
                            if (limit >= 100)
                            {
                                break;
                            }
                        }
                        grp.scriptScore = 0;
                    }
                }
            }

            return topScripts;
        }

        #endregion

        #region Other Methods

        protected internal void physicsBasedCrash()
        {
            handlerPhysicsCrash = UnRecoverableError;
            if (handlerPhysicsCrash != null)
            {
                handlerPhysicsCrash();
            }
        }

        protected internal UUID ConvertLocalIDToFullID(uint localID)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
                return group.GetPartsFullID(localID);
            else
                return UUID.Zero;
        }

        /// <summary>
        /// Performs action on all scene object groups.
        /// </summary>
        /// <param name="action"></param>
        protected internal void ForEachSOG(Action<SceneObjectGroup> action)
        {
            List<SceneObjectGroup> objlist = new List<SceneObjectGroup>(SceneObjectGroupsByFullID.Values);
            foreach (SceneObjectGroup obj in objlist)
            {
                try
                {
                    action(obj);
                }
                catch (Exception e)
                {
                    // Catch it and move on. This includes situations where splist has inconsistent info
                    m_log.WarnFormat("[SCENE]: Problem processing action in ForEachSOG: ", e.ToString());
                }
            }
        }

        
        /// <summary>
        /// Performs action on all scene presences. This can ultimately run the actions in parallel but
        /// any delegates passed in will need to implement their own locking on data they reference and
        /// modify outside of the scope of the delegate. 
        /// </summary>
        /// <param name="action"></param>
        public void ForEachScenePresence(Action<ScenePresence> action)
        {
            // Once all callers have their delegates configured for parallelism, we can unleash this
            /*
            Action<ScenePresence> protectedAction = new Action<ScenePresence>(delegate(ScenePresence sp)
                {
                    try
                    {
                        action(sp);
                    }
                    catch (Exception e)
                    {
                        m_log.Info("[BUG] in " + m_parentScene.RegionInfo.RegionName + ": " + e.ToString());
                        m_log.Info("[BUG] Stack Trace: " + e.StackTrace);
                    }
                });
            Parallel.ForEach<ScenePresence>(GetScenePresences(), protectedAction);
            */
            // For now, perform actions serially
            List<ScenePresence> presences = GetScenePresences();
            foreach (ScenePresence sp in presences)
            {
                try
                {
                    action(sp);
                }
                catch (Exception e)
                {
                    m_log.Info("[BUG] in " + m_parentScene.RegionInfo.RegionName + ": " + e.ToString());
                }
            }
        }
        
        #endregion

        #region Client Event handlers

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="scale"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimScale(uint localID, Vector3 scale, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    group.Resize(scale, localID);
                }
            }
        }

        protected internal void UpdatePrimGroupScale(uint localID, Vector3 scale, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    group.GroupResize(scale, localID);
                }
            }
        }

        /// <summary>
        /// This handles the nifty little tool tip that you get when you drag your mouse over an object
        /// Send to the Object Group to process.  We don't know enough to service the request
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="AgentID"></param>
        /// <param name="RequestFlags"></param>
        /// <param name="ObjectID"></param>
        protected internal void RequestObjectPropertiesFamily(
             IClientAPI remoteClient, UUID AgentID, uint RequestFlags, UUID ObjectID)
        {
            SceneObjectGroup group = GetGroupByPrim(ObjectID);
            if (group != null)
            {
                group.ServiceObjectPropertiesFamilyRequest(remoteClient, AgentID, RequestFlags);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSingleRotation(uint localID, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateSingleRotation(rot, localID);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSingleRotationPosition(uint localID, Quaternion rot, Vector3 pos, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateSingleRotation(rot,pos, localID);
                }
            }
        }


        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimRotation(uint localID, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateGroupRotationR(rot);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimRotation(uint localID, Vector3 pos, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateGroupRotationPR(pos, rot);
                }
            }
        }

        /// <summary>
        /// Update the position of the given part
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSinglePosition(uint localID, Vector3 pos, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId) || group.IsAttachment)
                {
                    group.UpdateSinglePosition(pos, localID);
                }
            }
        }

        /// <summary>
        /// Update the position of the given part
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        public void UpdatePrimPosition(uint localID, Vector3 pos, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            
            if (group != null)
            {
                if (group.IsAttachment || (group.RootPart.Shape.PCode == 9 && group.RootPart.Shape.State != 0))
                {
                    if (m_parentScene.AttachmentsModule != null)
                        m_parentScene.AttachmentsModule.UpdateAttachmentPosition(group, pos);
                }
                else
                {
                    if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId) 
                        && m_parentScene.Permissions.CanObjectEntry(group.UUID, false, pos))
                    {
                        group.UpdateGroupPosition(pos);
                    }
                }
            }
        }

        /// <summary>
        /// Update the texture entry of the given prim.
        /// </summary>
        /// 
        /// A texture entry is an object that contains details of all the textures of the prim's face.  In this case,
        /// the texture is given in its byte serialized form.
        /// 
        /// <param name="localID"></param>
        /// <param name="texture"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimTexture(uint localID, byte[] texture, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID,remoteClient.AgentId))
                {
                    group.UpdateTextureEntry(localID, texture);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="packet"></param>
        /// <param name="remoteClient"></param>
        /// This routine seems to get called when a user changes object settings in the viewer.
        /// If some one can confirm that, please change the comment according.
        protected internal void UpdatePrimFlags(uint localID, bool UsePhysics, bool IsTemporary, bool IsPhantom, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdatePrimFlags(localID, UsePhysics, IsTemporary, IsPhantom, false); // VolumeDetect can't be set via UI and will always be off when a change is made there
                }
            }
        }

        /// <summary>
        /// Move the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="offset"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void MoveObject(UUID objectID, Vector3 offset, Vector3 pos, IClientAPI remoteClient, List<SurfaceTouchEventArgs> surfaceArgs)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.GrabMovement(offset, pos, remoteClient);
                }
                // This is outside the above permissions condition
                // so that if the object is locked the client moving the object
                // get's it's position on the simulator even if it was the same as before
                // This keeps the moving user's client in sync with the rest of the world.
                group.SendGroupTerseUpdate();
            }
        }

        /// <summary>
        /// Start spinning the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="rotation"></param>
        /// <param name="remoteClient"></param>
        protected internal void SpinStart(UUID objectID, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.SpinStart(remoteClient);
                }
            }
        }

        /// <summary>
        /// Spin the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="rotation"></param>
        /// <param name="remoteClient"></param>
        protected internal void SpinObject(UUID objectID, Quaternion rotation, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.SpinMovement(rotation, remoteClient);
                }
                // This is outside the above permissions condition
                // so that if the object is locked the client moving the object
                // get's it's position on the simulator even if it was the same as before
                // This keeps the moving user's client in sync with the rest of the world.
                group.SendGroupTerseUpdate();
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="description"></param>
        protected internal void PrimName(IClientAPI remoteClient, uint primLocalID, string name)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    group.SetPartName(Util.CleanString(name), primLocalID);
                    group.HasGroupChanged = true;
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="description"></param>
        protected internal void PrimDescription(IClientAPI remoteClient, uint primLocalID, string description)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    group.SetPartDescription(Util.CleanString(description), primLocalID);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void PrimClickAction(IClientAPI remoteClient, uint primLocalID, string clickAction)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    SceneObjectPart part = m_parentScene.GetSceneObjectPart(primLocalID);
                    part.ClickAction = Convert.ToByte(clickAction);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void PrimMaterial(IClientAPI remoteClient, uint primLocalID, string material)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId))
                {
                    SceneObjectPart part = m_parentScene.GetSceneObjectPart(primLocalID);
                    part.Material = Convert.ToByte(material);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void UpdateExtraParam(UUID agentID, uint primLocalID, ushort type, bool inUse, byte[] data)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);

            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID,agentID))
                {
                    group.UpdateExtraParam(primLocalID, type, inUse, data);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="shapeBlock"></param>
        protected internal void UpdatePrimShape(UUID agentID, uint primLocalID, UpdateShapeArgs shapeBlock)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.GetPartsFullID(primLocalID), agentID))
                {
                    ObjectShapePacket.ObjectDataBlock shapeData = new ObjectShapePacket.ObjectDataBlock();
                    shapeData.ObjectLocalID = shapeBlock.ObjectLocalID;
                    shapeData.PathBegin = shapeBlock.PathBegin;
                    shapeData.PathCurve = shapeBlock.PathCurve;
                    shapeData.PathEnd = shapeBlock.PathEnd;
                    shapeData.PathRadiusOffset = shapeBlock.PathRadiusOffset;
                    shapeData.PathRevolutions = shapeBlock.PathRevolutions;
                    shapeData.PathScaleX = shapeBlock.PathScaleX;
                    shapeData.PathScaleY = shapeBlock.PathScaleY;
                    shapeData.PathShearX = shapeBlock.PathShearX;
                    shapeData.PathShearY = shapeBlock.PathShearY;
                    shapeData.PathSkew = shapeBlock.PathSkew;
                    shapeData.PathTaperX = shapeBlock.PathTaperX;
                    shapeData.PathTaperY = shapeBlock.PathTaperY;
                    shapeData.PathTwist = shapeBlock.PathTwist;
                    shapeData.PathTwistBegin = shapeBlock.PathTwistBegin;
                    shapeData.ProfileBegin = shapeBlock.ProfileBegin;
                    shapeData.ProfileCurve = shapeBlock.ProfileCurve;
                    shapeData.ProfileEnd = shapeBlock.ProfileEnd;
                    shapeData.ProfileHollow = shapeBlock.ProfileHollow;

                    group.UpdateShape(shapeData, primLocalID);
                }
            }
        }

        /// <summary>
        /// Initial method invoked when we receive a link objects request from the client.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="parentPrim"></param>
        /// <param name="childPrims"></param>
        protected internal void LinkObjects(SceneObjectPart root, List<SceneObjectPart> children)
        {
            Monitor.Enter(m_updateLock);
            try
            {
                SceneObjectGroup parentGroup = root.ParentGroup;

                List<SceneObjectGroup> childGroups = new List<SceneObjectGroup>();
                if (parentGroup != null)
                {
                    // We do this in reverse to get the link order of the prims correct
                    for (int i = children.Count - 1; i >= 0; i--)
                    {
                        SceneObjectGroup child = children[i].ParentGroup;

                        if (child != null)
                        {
                            // Make sure no child prim is set for sale
                            // So that, on delink, no prims are unwittingly
                            // left for sale and sold off
                            child.RootPart.ObjectSaleType = 0;
                            child.RootPart.SalePrice = 10;
                            childGroups.Add(child);
                        }
                    }
                }
                else
                {
                    return; // parent is null so not in this region
                }



                foreach (SceneObjectGroup child in childGroups)
                {
                    m_log.Debug("linking child " + child.UUID + " to parentGroup " + parentGroup.UUID);

                    parentGroup.LinkToGroup(child);

                    // this is here so physics gets updated!
                    // Don't remove!  Bad juju!  Stay away! or fix physics!
                    child.AbsolutePosition = child.AbsolutePosition;
                }

                parentGroup.RootPart.CreateSelected = true;
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
                parentGroup.HasGroupChanged = true;

                //SYMMETRIC SYNC
                //Schedule a LinkObject message for synchronization purpose. This will lead to enqueue a LinkObject message in SyncConnector's outgoingQueue,
                //so should return quickly. 
                if (m_parentScene.RegionSyncModule != null)
                {
                    //Tell other actors to link the SceneObjectParts together as a new group. 
                    m_parentScene.RegionSyncModule.SendLinkObject(parentGroup, root, children);
                }

                //Schedule updates as in legacy OpenSim code, to send updates to viewers connected to this actor (at least needed for client managers).
                //But timestamp won't be changed, so that when other actors get the update, they's simple ignore the updates since they already get them
                //via the LinkObject message sent above.
                parentGroup.ScheduleGroupForFullUpdate_SyncInfoUnchanged();

                //end of SYMMETRIC SYNC
                
            }
            finally
            {
                Monitor.Exit(m_updateLock);
            }
        }

        /// <summary>
        /// Delink a linkset
        /// </summary>
        /// <param name="prims"></param>
        protected internal void DelinkObjects(List<SceneObjectPart> prims)
        {
            Monitor.Enter(m_updateLock);
            try
            {
                List<SceneObjectPart> childParts = new List<SceneObjectPart>();
                List<SceneObjectPart> rootParts = new List<SceneObjectPart>();
                List<SceneObjectGroup> affectedGroups = new List<SceneObjectGroup>();

                //SYMMETRIC SYNC, record the new object groups after the delink operation
                List<SceneObjectGroup> beforeDelinkGroups = new List<SceneObjectGroup>();
                List<SceneObjectGroup> afterDelinkGroups = new List<SceneObjectGroup>();

                // Look them all up in one go, since that is comparatively expensive
                //
                foreach (SceneObjectPart part in prims)
                {
                    if (part != null)
                    {
                        if (part.ParentGroup.PrimCount != 1) // Skip single
                        {
                            if (part.LinkNum < 2) // Root
                                rootParts.Add(part);
                            else
                                childParts.Add(part);

                            SceneObjectGroup group = part.ParentGroup;
                            if (!affectedGroups.Contains(group))
                            {
                                affectedGroups.Add(group);
                                //SYMMETRIC SYNC
                                beforeDelinkGroups.Add(group);
                            }
                        }
                    }
                }

                foreach (SceneObjectPart child in childParts)
                {
                    // Unlink all child parts from their groups
                    //
                    child.ParentGroup.DelinkFromGroup(child, true);

                    // These are not in affected groups and will not be
                    // handled further. Do the honors here.
                    child.ParentGroup.HasGroupChanged = true;

                    //SYMMETRIC SYNC, delay ScheduleGroupForFullUpdate till the end of the delink operations. 
                    //child.ParentGroup.ScheduleGroupForFullUpdate();
                    afterDelinkGroups.Add(child.ParentGroup);
                }

                foreach (SceneObjectPart root in rootParts)
                {
                    // In most cases, this will run only one time, and the prim
                    // will be a solo prim
                    // However, editing linked parts and unlinking may be different
                    //
                    SceneObjectGroup group = root.ParentGroup;
                    
                    List<SceneObjectPart> newSet = new List<SceneObjectPart>(group.Parts);
                    int numChildren = newSet.Count;

                    // If there are prims left in a link set, but the root is
                    // slated for unlink, we need to do this
                    //
                    if (numChildren != 1)
                    {
                        // Unlink the remaining set
                        //
                        bool sendEventsToRemainder = true;
                        if (numChildren > 1)
                            sendEventsToRemainder = false;

                        foreach (SceneObjectPart p in newSet)
                        {
                            if (p != group.RootPart)
                                group.DelinkFromGroup(p, sendEventsToRemainder);
                        }

                        // If there is more than one prim remaining, we
                        // need to re-link
                        //
                        if (numChildren > 2)
                        {
                            // Remove old root
                            //
                            if (newSet.Contains(root))
                                newSet.Remove(root);

                            // Preserve link ordering
                            //
                            newSet.Sort(delegate (SceneObjectPart a, SceneObjectPart b)
                            {
                                return a.LinkNum.CompareTo(b.LinkNum);
                            });

                            // Determine new root
                            //
                            SceneObjectPart newRoot = newSet[0];
                            newSet.RemoveAt(0);

                            foreach (SceneObjectPart newChild in newSet)
                                newChild.UpdateFlag = 0;

                            LinkObjects(newRoot, newSet);
                            if (!affectedGroups.Contains(newRoot.ParentGroup))
                                affectedGroups.Add(newRoot.ParentGroup);
                        }
                    }
                }

                // Finally, trigger events in the roots
                //
                foreach (SceneObjectGroup g in affectedGroups)
                {
                    g.TriggerScriptChangedEvent(Changed.LINK);
                    g.HasGroupChanged = true; // Persist

                    //SYMMETRIC SYNC, delay ScheduleGroupForFullUpdate till the end of the delink operations. 
                    //g.ScheduleGroupForFullUpdate();
                    afterDelinkGroups.Add(g);
                }

                //SYMMETRIC SYNC
                //Send out DelinkObject message to other actors to sychronize their object list 
                if (m_parentScene.RegionSyncModule != null)
                {
                    m_parentScene.RegionSyncModule.SendDeLinkObject(prims, beforeDelinkGroups, afterDelinkGroups);
                }
                //Schedule updates as in legacy OpenSim code, to send updates to viewers connected to this actor (at least needed for client managers).
                //But timestamp won't be changed, so that when other actors get the update, they's simple ignore the updates since they already get them
                foreach (SceneObjectGroup sog in afterDelinkGroups)
                {
                    sog.ScheduleGroupForFullUpdate_SyncInfoUnchanged();
                }
                //end of SYMMETRIC SYNC
            }
            finally
            {
                Monitor.Exit(m_updateLock);
            }
        }

        protected internal void MakeObjectSearchable(IClientAPI remoteClient, bool IncludeInSearch, uint localID)
        {
            UUID user = remoteClient.AgentId;
            UUID objid = UUID.Zero;
            SceneObjectPart obj = null;

            EntityBase[] entityList = GetEntities();
            foreach (EntityBase ent in entityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectGroup sog = ent as SceneObjectGroup;

                    foreach (SceneObjectPart part in sog.Parts)
                    {
                        if (part.LocalId == localID)
                        {
                            objid = part.UUID;
                            obj = part;
                        }
                    }
                }
            }

            //Protip: In my day, we didn't call them searchable objects, we called them limited point-to-point joints
            //aka ObjectFlags.JointWheel = IncludeInSearch

            //Permissions model: Object can be REMOVED from search IFF:
            // * User owns object
            //use CanEditObject

            //Object can be ADDED to search IFF:
            // * User owns object
            // * Asset/DRM permission bit "modify" is enabled
            //use CanEditObjectPosition

            // libomv will complain about PrimFlags.JointWheel being
            // deprecated, so we
            #pragma warning disable 0612
            if (IncludeInSearch && m_parentScene.Permissions.CanEditObject(objid, user))
            {
                obj.ParentGroup.RootPart.AddFlag(PrimFlags.JointWheel);
                obj.ParentGroup.HasGroupChanged = true;
            }
            else if (!IncludeInSearch && m_parentScene.Permissions.CanMoveObject(objid,user))
            {
                obj.ParentGroup.RootPart.RemFlag(PrimFlags.JointWheel);
                obj.ParentGroup.HasGroupChanged = true;
            }
            #pragma warning restore 0612
        }

        /// <summary>
        /// Duplicate the given object, Fire and Forget, No rotation, no return wrapper
        /// </summary>
        /// <param name="originalPrim"></param>
        /// <param name="offset"></param>
        /// <param name="flags"></param>
        /// <param name="AgentID"></param>
        /// <param name="GroupID"></param>
        protected internal void DuplicateObject(uint originalPrim, Vector3 offset, uint flags, UUID AgentID, UUID GroupID)
        {
            //m_log.DebugFormat("[SCENE]: Duplication of object {0} at offset {1} requested by agent {2}", originalPrim, offset, AgentID);

            // SceneObjectGroup dupe = DuplicateObject(originalPrim, offset, flags, AgentID, GroupID, Quaternion.Zero);
            DuplicateObject(originalPrim, offset, flags, AgentID, GroupID, Quaternion.Identity);
        }
        
        /// <summary>
        /// Duplicate the given object.
        /// </summary>
        /// <param name="originalPrim"></param>
        /// <param name="offset"></param>
        /// <param name="flags"></param>
        /// <param name="AgentID"></param>
        /// <param name="GroupID"></param>
        /// <param name="rot"></param>
        public SceneObjectGroup DuplicateObject(uint originalPrimID, Vector3 offset, uint flags, UUID AgentID, UUID GroupID, Quaternion rot)
        {
            //m_log.DebugFormat("[SCENE]: Duplication of object {0} at offset {1} requested by agent {2}", originalPrim, offset, AgentID);
            SceneObjectGroup original = GetGroupByPrim(originalPrimID);
            if (original != null)
            {
                if (m_parentScene.Permissions.CanDuplicateObject(
                    original.PrimCount, original.UUID, AgentID, original.AbsolutePosition))
                {
                    SceneObjectGroup copy = original.Copy(true);
                    copy.AbsolutePosition = copy.AbsolutePosition + offset;

                    if (original.OwnerID != AgentID)
                    {
                        copy.SetOwnerId(AgentID);
                        copy.SetRootPartOwner(copy.RootPart, AgentID, GroupID);

                        SceneObjectPart[] partList = copy.Parts;

                        if (m_parentScene.Permissions.PropagatePermissions())
                        {
                            foreach (SceneObjectPart child in partList)
                            {
                                child.Inventory.ChangeInventoryOwner(AgentID);
                                child.TriggerScriptChangedEvent(Changed.OWNER);
                                child.ApplyNextOwnerPermissions();
                            }
                        }

                        copy.RootPart.ObjectSaleType = 0;
                        copy.RootPart.SalePrice = 10;
                    }

                    Entities.Add(copy);

                    // Since we copy from a source group that is in selected
                    // state, but the copy is shown deselected in the viewer,
                    // We need to clear the selection flag here, else that
                    // prim never gets persisted at all. The client doesn't
                    // think it's selected, so it will never send a deselect...
                    copy.IsSelected = false;

                    m_numPrim += copy.Parts.Length;

                    if (rot != Quaternion.Identity)
                    {
                        copy.UpdateGroupRotationR(rot);
                    }

                    copy.CreateScriptInstances(0, false, m_parentScene.DefaultScriptEngine, 1);
                    copy.HasGroupChanged = true;
                    //copy.ScheduleGroupForFullUpdate();
                    copy.ScheduleGroupForFullUpdate(new List<SceneObjectPartProperties>(){SceneObjectPartProperties.FullUpdate}); //new object, all property values are new
                    copy.ResumeScripts();

                    // required for physics to update it's position
                    copy.AbsolutePosition = copy.AbsolutePosition;

                    if (OnObjectDuplicate != null)
                        OnObjectDuplicate(original, copy);

                    return copy;
                }
            }
            else
            {
                m_log.WarnFormat("[SCENE]: Attempted to duplicate nonexistant prim id {0}", GroupID);
            }
            
            return null;
        }
        
        /// <summary>
        /// Calculates the distance between two Vector3s
        /// </summary>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <returns></returns>
        protected internal float Vector3Distance(Vector3 v1, Vector3 v2)
        {
            // We don't really need the double floating point precision...
            // so casting it to a single

            return
                (float)
                Math.Sqrt((v1.X - v2.X) * (v1.X - v2.X) + (v1.Y - v2.Y) * (v1.Y - v2.Y) + (v1.Z - v2.Z) * (v1.Z - v2.Z));
        }

        #endregion

        #region REGION SYNC

        protected internal bool IsObjectInScene(SceneObjectGroup sog)
        {
            if (Entities.ContainsKey(sog.UUID))
                return true;
            else
            {
                return false;
            }
        }
        
        //Return false if the entity with the UUID is not a SceneObjectGroup, 
        //otherwise, return true.
        protected internal bool AddOrUpdateObjectInScene(SceneObjectGroup updatedSog, bool debugWithViewer)
        {
            UUID sogID = updatedSog.UUID;

            if (Entities.ContainsKey(sogID))
            {
                //update the object
                EntityBase entity = Entities[sogID];
                if (entity is SceneObjectGroup)
                {
                    SceneObjectGroup oldSog = (SceneObjectGroup)entity;
                    oldSog.UpdateObjectProperties(updatedSog);

                    if (debugWithViewer)
                    {
                        //if we need to debug the script engine with a viewer attaching to it,
                        //we need to schedule updates to be sent to the viewer
                        oldSog.ScheduleGroupForFullUpdate(new List<SceneObjectPartProperties>(){SceneObjectPartProperties.None});
                    }
                }
                else
                {
                    m_log.WarnFormat("{0}: Entity with {1} is not of type SceneObjectGroup: {2}",
                                    "[SCENE GRAPH]", sogID, entity.GetType().ToString());
                    //return false;
                }
                return false;
            }
            else
            {
                //Add a new object
                //For now, we set sendClientUpdates to true, for debugging purpose -- so that we could log a viewer in to 
                //see if scripts are running properly
                //Since this is a Script Engine's local Scene cache, do not backup to DB
                AddSceneObject(updatedSog, false, debugWithViewer);
                //AddSceneObject(updatedSog, false, false);

                return true;
            }

        }

        #endregion // REGION SYNC

        #region SYMMETRIC SYNC

        public Scene.ObjectUpdateResult AddOrUpdateObjectBySynchronization(SceneObjectGroup updatedSog)
        {
            UUID sogID = updatedSog.UUID;
            Scene.ObjectUpdateResult updateResult = Scene.ObjectUpdateResult.Unchanged;

            if (Entities.ContainsKey(sogID))
            {
                //update the object
                EntityBase entity = Entities[sogID];
                if (entity is SceneObjectGroup)
                {
                    SceneObjectGroup localSog = (SceneObjectGroup)entity;
                    updateResult = localSog.UpdateObjectGroupBySync(updatedSog);
                }
                else
                {
                    m_log.WarnFormat("{0}: Entity with {1} iss not of type SceneObjectGroup: {2}",
                                    "[SCENE GRAPH]", sogID, entity.GetType().ToString());
                    //return false;
                    updateResult = Scene.ObjectUpdateResult.Error;
                }
            }
            else
            {
                m_log.Debug("AddSceneObjectByStateSynch to be called");
                AddSceneObjectByStateSynch(updatedSog);
                updateResult = Scene.ObjectUpdateResult.New;
            }

            //Debug
            //m_log.Debug("after AddOrUpdateObjectBySynchronization");
            //m_parentScene.DebugSceneObjectGroups();

            return updateResult;
        }

        //This is called when an object is added due to receiving a state synchronization message from Scene or an actor. Do similar things as the original AddSceneObject(),
        //but call ScheduleGroupForFullUpdate_TimeStampUnchanged() instead, so as not to modify the timestamp or actorID, since the object was not created locally.
        public bool AddSceneObjectByStateSynch(SceneObjectGroup sceneObject)
        {
            if (sceneObject == null || sceneObject.RootPart == null || sceneObject.RootPart.UUID == UUID.Zero)
                return false;

            if (Entities.ContainsKey(sceneObject.UUID))
                return false;

            SceneObjectPart[] children = sceneObject.Parts;

            // Clamp child prim sizes and add child prims to the m_numPrim count
            if (m_parentScene.m_clampPrimSize)
            {
                foreach (SceneObjectPart part in children)
                {
                    SceneObjectPartBase partBase = (SceneObjectPartBase)part;
                    Vector3 scale = partBase.Scale;

                    if (scale.X > m_parentScene.m_maxNonphys)
                        scale.X = m_parentScene.m_maxNonphys;
                    if (scale.Y > m_parentScene.m_maxNonphys)
                        scale.Y = m_parentScene.m_maxNonphys;
                    if (scale.Z > m_parentScene.m_maxNonphys)
                        scale.Z = m_parentScene.m_maxNonphys;

                    part.Scale = scale;
                }
            }
            m_numPrim += children.Length;

            sceneObject.AttachToScene(m_parentScene);

            //Take some special care of the case of this object being an attachment
            sceneObject.RootPart.SetAttachmentPoint(sceneObject.RootPart.AttachmentPoint); 
            if (sceneObject.IsAttachment)
            {
                ScenePresence avatar = m_parentScene.GetScenePresence(sceneObject.RootPart.AttachedAvatar);
                sceneObject.RootPart.SetParentLocalId(avatar.LocalId);
            }

            //SYMMETRIC SYNC, 
            sceneObject.ScheduleGroupForFullUpdate_SyncInfoUnchanged();
            //end of SYMMETRIC SYNC, 

            Entities.Add(sceneObject);

            //ScenePersistenceSyncModule will attach the object to backup when it catches the OnObjectCreate event.
            //if (attachToBackup)
            //    sceneObject.AttachToBackup();

            //if (OnObjectCreate != null)
            //    OnObjectCreate(sceneObject);

            if (OnObjectCreateBySync != null)
                OnObjectCreateBySync(sceneObject);


            lock (SceneObjectGroupsByFullID)
            {
                SceneObjectGroupsByFullID[sceneObject.UUID] = sceneObject;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByFullID[part.UUID] = sceneObject;
            }

            lock (SceneObjectGroupsByLocalID)
            {
                SceneObjectGroupsByLocalID[sceneObject.LocalId] = sceneObject;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByLocalID[part.LocalId] = sceneObject;
            }

            return true;
        }

        public void AddNewSceneObjectPart(SceneObjectPart newPart, SceneObjectGroup parentGroup)
        {
            SceneObjectPart[] children = parentGroup.Parts;

            lock (SceneObjectGroupsByFullID)
            {
                SceneObjectGroupsByFullID[parentGroup.UUID] = parentGroup;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByFullID[newPart.UUID] = parentGroup;
            }

            lock (SceneObjectGroupsByLocalID)
            {
                SceneObjectGroupsByLocalID[parentGroup.LocalId] = parentGroup;
                foreach (SceneObjectPart part in children)
                    SceneObjectGroupsByLocalID[newPart.LocalId] = parentGroup;
            }
        }

        public void LinkObjectsBySync(SceneObjectPart root, List<SceneObjectPart> children)
        {
            Monitor.Enter(m_updateLock);
            try
            {
                SceneObjectGroup parentGroup = root.ParentGroup;

                List<SceneObjectGroup> childGroups = new List<SceneObjectGroup>();
                if (parentGroup != null)
                {
                    // We do this in reverse to get the link order of the prims correct
                    for (int i = children.Count - 1; i >= 0; i--)
                    {
                        SceneObjectGroup child = children[i].ParentGroup;

                        if (child != null)
                        {
                            // Make sure no child prim is set for sale
                            // So that, on delink, no prims are unwittingly
                            // left for sale and sold off
                            //SYMMETRIC SYNC: need to copy value w/o trigger UpdateBucketSyncInfo
                            //child.RootPart.ObjectSaleType = 0;
                            //child.RootPart.SalePrice = 10;
                            //child.RootPart.SetObjectSaleType(0);
                            //child.RootPart.SetSalePrice(10);
                            //child.RootPart.SetProperty("ObjectSaleType", 0);
                            //child.RootPart.SetProperty("SalePrice", 10);

                            //casting SOP to SOPBase to make sure we call SOPBase.Property set function, not the SOP.Property set function
                            SceneObjectPartBase rootPart = (SceneObjectPartBase)child.RootPart;
                            rootPart.ObjectSaleType = 0;
                            rootPart.SalePrice = 10;
                            childGroups.Add(child);
                        }
                    }
                }
                else
                {
                    return; // parent is null so not in this region
                }

                foreach (SceneObjectGroup child in childGroups)
                {
                    parentGroup.LinkToGroupBySync(child);

                    // this is here so physics gets updated!
                    // Don't remove!  Bad juju!  Stay away! or fix physics!
                    child.AbsolutePosition = child.AbsolutePosition;
                }


                // We need to explicitly resend the newly link prim's object properties since no other actions
                // occur on link to invoke this elsewhere (such as object selection)
                parentGroup.RootPart.CreateSelected = true;
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
                parentGroup.HasGroupChanged = true;
                //Do not change the timestamp and actorID values
                parentGroup.ScheduleGroupForFullUpdate_SyncInfoUnchanged();

            }
            finally
            {
                Monitor.Exit(m_updateLock);
            }
        }


        /// <summary>
        /// Delink the prims as indicated in localPrimIDs, and regroup them as the object-groups indicated in incomingAfterDelinkGroups.
        /// </summary>
        /// <param name="localPrims"></param>
        /// <param name="beforeDelinkGroupIDs"></param>
        /// <param name="incomingAfterDelinkGroups"></param>
        public void DelinkObjectsBySync(List<UUID> delinkPrimIDs, List<UUID> beforeDelinkGroupIDs, List<SceneObjectGroup> incomingAfterDelinkGroups)
        {
            Dictionary<UUID, SceneObjectGroup> localBeforeDelinkGroups = new Dictionary<UUID, SceneObjectGroup>();
            Dictionary<UUID, SceneObjectPart> delinkPrims = new Dictionary<UUID, SceneObjectPart>();
            bool beforeStateConsistent = true;
            bool afterStateConsistent = true;

            Monitor.Enter(m_updateLock);
            try
            {
                //get the before-delink-groups, and all the prims to delink
                foreach (UUID primID in delinkPrimIDs)
                {
                    SceneObjectPart localPart = GetSceneObjectPart(primID);
                    if (!delinkPrims.ContainsKey(primID))
                    {
                        delinkPrims.Add(primID, localPart);
                    }
                    SceneObjectGroup localGroup = localPart.ParentGroup;
                    if (!localBeforeDelinkGroups.ContainsKey(localGroup.UUID))
                    {
                        localBeforeDelinkGroups.Add(localGroup.UUID, localGroup);
                    }
                }

                //Next, do some sanity check to see if the local copy agrees with remote copy on the before-link state. 
                //TODO:: Actions to be taken after detecting conflicts. For now, we just assume the chance that conflict will happen is almost 0.

                //First, check if the groups match
                if (beforeDelinkGroupIDs.Count != localBeforeDelinkGroups.Count)
                {
                    //detected conflict on editing object groups
                    m_log.Warn("DelinkObjectsBySync: the # of groups in before-delink-groups is different from the incoming delink message. NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                    beforeStateConsistent = false;
                    //TODO: further actions
                }
                else
                {
                    foreach (UUID beforeGroupID in beforeDelinkGroupIDs)
                    {
                        if (!localBeforeDelinkGroups.ContainsKey(beforeGroupID))
                        {
                            m_log.Warn("DelinkObjectsBySync: the local state of before-delink-groups is different from the incoming delink message. NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                            beforeStateConsistent = false;
                            break;
                        }
                    }
                    //TODO: further actions
                }

                if(beforeStateConsistent){
                    //Second, check if the prims match
                    List<SceneObjectPart> allPrimsInLocalGroups = new List<SceneObjectPart>();
                    foreach (KeyValuePair<UUID, SceneObjectGroup> pair in localBeforeDelinkGroups)
                    {
                        foreach (SceneObjectPart part in pair.Value.Parts)
                        {
                            allPrimsInLocalGroups.Add(part);
                        }
                    }
                    if (allPrimsInLocalGroups.Count != delinkPrims.Count)
                    {
                        m_log.Warn("DelinkObjectsBySync: the # of prims of before-delink-groups is different from the incoming delink message. NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                        beforeStateConsistent = false;
                        //TODO: further action
                    }else{
                        foreach (SceneObjectPart part in allPrimsInLocalGroups)
                        {
                            if (!delinkPrims.ContainsKey(part.UUID))
                            {
                                m_log.Warn("DelinkObjectsBySync: some local prims in before-delink-groups not exist in the incoming delink message. NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                                beforeStateConsistent = false;
                                break;
                                //TODO: further action
                            }
                        }
                    }
                }
                //end of sanity checking

                if(!beforeStateConsistent){
                    m_log.Warn("DelinkObjectsBySync: before-delink state not consistent in local copy and the incoming copy. Return without further operations.");
                }else{
                    //Next, apply the delink operation locally.
                    List<SceneObjectGroup> localAfterDelinkGroups = DelinkObjectsBySync(new List<SceneObjectPart>(delinkPrims.Values));

                    
                    //Check if local after-state agrees with that in the remote copy, and update the groups' properties
                    if (localAfterDelinkGroups.Count != incomingAfterDelinkGroups.Count)
                    {
                        m_log.Warn("DelinkObjectsBySync: local state after delink does not agree with the incoming delink message (# of groups are different). NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                        afterStateConsistent = false;
                    }
                    else
                    {
                        Dictionary<UUID, SceneObjectGroup> incomingAfterDelinkGroupsDictionary = new Dictionary<UUID,SceneObjectGroup>();
                        foreach (SceneObjectGroup incomingGroup in incomingAfterDelinkGroups){
                            incomingAfterDelinkGroupsDictionary.Add(incomingGroup.UUID, incomingGroup);
                        }
                        foreach (SceneObjectGroup localAfterGroup in localAfterDelinkGroups)
                        {
                            if (!incomingAfterDelinkGroupsDictionary.ContainsKey(localAfterGroup.UUID))
                            {
                                m_log.Warn("DelinkObjectsBySync: local state after delink does not agree with the incoming delink message. NEED BETTER CONCURRENCY CONTROL IMPLEMENTATION!!!");
                                afterStateConsistent = false;
                            }
                            else
                            {
                                localAfterGroup.UpdateObjectGroupBySync(incomingAfterDelinkGroupsDictionary[localAfterGroup.UUID]);
                            }
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(m_updateLock);
            }

            if(beforeStateConsistent && afterStateConsistent){
                m_log.Debug("DelinkObjectsBySync successful");
            }
        }

        //Similar to DelinkObjects(), w/o triggering any ScheduleFullUpdate(),
        private List<SceneObjectGroup> DelinkObjectsBySync(List<SceneObjectPart> prims)
        {
            //!!!Caller of this function should already lock on m_updateLock, so no locking here !!!

            List<SceneObjectGroup> afterDelinkGroups = new List<SceneObjectGroup>();

            List<SceneObjectPart> childParts = new List<SceneObjectPart>();
            List<SceneObjectPart> rootParts = new List<SceneObjectPart>();
            List<SceneObjectGroup> affectedGroups = new List<SceneObjectGroup>();

            foreach (SceneObjectPart part in prims)
            {
                if (part != null)
                {
                    if (part.ParentGroup.PrimCount != 1) // Skip single
                    {
                        if (part.LinkNum < 2) // Root
                            rootParts.Add(part);
                        else
                            childParts.Add(part);

                        SceneObjectGroup group = part.ParentGroup;
                        if (!affectedGroups.Contains(group))
                        {
                            affectedGroups.Add(group);
                        }
                    }
                }
            }

            foreach (SceneObjectPart child in childParts)
            {
                // Unlink all child parts from their groups, w/o triggering unwanted events or syncinfo updates
                child.ParentGroup.DelinkFromGroupBySync(child, true);

                // These are not in affected groups and will not be
                // handled further. Do the honors here.
                child.ParentGroup.HasGroupChanged = true;

                //record the after-delink-groups
                afterDelinkGroups.Add(child.ParentGroup);
            }

            foreach (SceneObjectPart root in rootParts)
            {
                // In most cases, this will run only one time, and the prim
                // will be a solo prim
                // However, editing linked parts and unlinking may be different
                //
                SceneObjectGroup group = root.ParentGroup;

                List<SceneObjectPart> newSet = new List<SceneObjectPart>(group.Parts);
                int numChildren = newSet.Count;

                // If there are prims left in a link set, but the root is
                // slated for unlink, we need to do this
                //
                if (numChildren != 1)
                {
                    // Unlink the remaining set
                    //
                    bool sendEventsToRemainder = true;
                    if (numChildren > 1)
                        sendEventsToRemainder = false;

                    foreach (SceneObjectPart p in newSet)
                    {
                        if (p != group.RootPart)
                            group.DelinkFromGroupBySync(p, sendEventsToRemainder);
                    }

                    // If there is more than one prim remaining, we
                    // need to re-link
                    //
                    if (numChildren > 2)
                    {
                        // Remove old root
                        //
                        if (newSet.Contains(root))
                            newSet.Remove(root);

                        // Preserve link ordering
                        //
                        newSet.Sort(delegate(SceneObjectPart a, SceneObjectPart b)
                        {
                            return a.LinkNum.CompareTo(b.LinkNum);
                        });

                        // Determine new root
                        //
                        SceneObjectPart newRoot = newSet[0];
                        newSet.RemoveAt(0);

                        foreach (SceneObjectPart newChild in newSet)
                            newChild.UpdateFlag = 0;

                        LinkObjectsBySync(newRoot, newSet);
                        if (!affectedGroups.Contains(newRoot.ParentGroup))
                            affectedGroups.Add(newRoot.ParentGroup);
                    }
                }
            }

            foreach (SceneObjectGroup g in affectedGroups)
            {
                g.TriggerScriptChangedEvent(Changed.LINK);
                g.HasGroupChanged = true; // Persist

                afterDelinkGroups.Add(g);
            }

            return afterDelinkGroups;
        }

        //public Scene.ObjectUpdateResult UpdateObjectPartBucketProperties(string bucketName, UUID partUUID, Dictionary<string, Object> updatedProperties, BucketSyncInfo rBucketSyncInfo)
        public Scene.ObjectUpdateResult UpdateObjectPartBucketProperties(string bucketName, UUID partUUID, 
                            Object updatedPart, BucketSyncInfo bucketSyncInfo)
        {
            SceneObjectPart localPart = GetSceneObjectPart(partUUID);
            if (localPart == null)
            {
                m_log.Warn("No SOP found: UUID -- " + partUUID);
                return Scene.ObjectUpdateResult.Unchanged;
            }
            return localPart.UpdateBucketProperties(bucketName, updatedPart, bucketSyncInfo);
        }

        #endregion //SYMMETRIC SYNC
    }
}
