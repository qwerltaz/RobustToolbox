﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GameObject;
using Lidgren.Network;
using SS13_Shared;
using ServerInterfaces.Network;
using SS13_Shared.GO;

namespace SGO
{
    /// <summary>
    /// Manager for entities -- controls things like template loading and instantiation
    /// </summary>
    public class EntityManager : GameObject.EntityManager, ServerInterfaces.GOC.IEntityManager
    {
        private static EntityManager singleton;
        private readonly ISS13NetServer m_netServer;
        private EntityFactory m_entityFactory;
        private EntityTemplateDatabase m_entityTemplateDatabase;
        private EntitySystemManager _systemManager;
        public int nextId;
        private bool _initialized;
        private Queue<IncomingEntityMessage> MessageBuffer = new Queue<IncomingEntityMessage>();

        public EntityManager(ISS13NetServer netServer)
            :base("SGO", new EntityNetworkManager(netServer))
        {
            EngineType = EngineType.Server;
            m_entityTemplateDatabase = new EntityTemplateDatabase(this);
            m_entityFactory = new EntityFactory(m_entityTemplateDatabase);
            m_netServer = netServer;
            _systemManager = new EntitySystemManager(this);
            LoadEntities();
            _systemManager.Initialize();
            _initialized = true;
        }

        #region IEntityManager Members
        
        public void SaveEntities()
        {
            //List<XElement> entities = new List<XElement>();
            IEnumerable<XElement> entities = from Entity e in _entities.Values
                                             where e.Template.Name != "HumanMob"
                                             select ToXML(e);

            var saveFile = new XDocument(new XElement("SavedEntities", entities.ToArray()));
            saveFile.Save("SavedEntities.xml");
        }

        public void SendEntities(NetConnection client)
        {
            foreach(Entity e in _entities.Values)
            {
                //e.FireNetworkedJoinSpawn(client);
            }
        }

        /// <summary>
        /// Creates an entity and adds it to the entity dictionary
        /// </summary>
        /// <param name="EntityType">name of entity template to execute</param>
        /// <returns>spawned entity</returns>
        public Entity SpawnEntity(string EntityType, bool send = true)
        {
            var e = m_entityFactory.CreateEntity(EntityType);
            if (e != null)
            {
                e.Uid = nextId++;
                _entities.Add(e.Uid, e);
                if (send) e.Initialize();
                //if (send) e.FireNetworkedSpawn();
            }
            return e;
        }



        public void Shutdown()
        {
            FlushEntities();
            _systemManager.Shutdown();
            _systemManager = null;
            m_entityFactory = null;
            m_entityTemplateDatabase = null;
            EntityNetworkManager = null;
        }

        private void ProcessMsgBuffer()
        {
            if (!_initialized)
                return;
            if (!MessageBuffer.Any()) return;
            var misses = new List<IncomingEntityMessage>();

            while (MessageBuffer.Any())
            {
                IncomingEntityMessage entMsg = MessageBuffer.Dequeue();
                if (!_entities.ContainsKey(entMsg.Uid))
                {
                    entMsg.LastProcessingAttempt = DateTime.Now;
                    if ((entMsg.LastProcessingAttempt - entMsg.ReceivedTime).TotalSeconds > entMsg.Expires)
                        misses.Add(entMsg);
                }
                else
                    _entities[entMsg.Uid].HandleNetworkMessage(entMsg);
            }

            foreach (var miss in misses)
                MessageBuffer.Enqueue(miss);

            MessageBuffer.Clear(); //Should be empty at this point anyway.
        }

        private IncomingEntityMessage ProcessNetMessage(NetIncomingMessage msg)
        {
            return EntityNetworkManager.HandleEntityNetworkMessage(msg);
        }

        /// <summary>
        /// Handle an incoming network message by passing the message to the EntityNetworkManager 
        /// and handling the parsed result.
        /// </summary>
        /// <param name="msg">Incoming raw network message</param>
        public void HandleEntityNetworkMessage(NetIncomingMessage msg)
        {
            if (!_initialized)
            {
                var emsg = ProcessNetMessage(msg);
                if (emsg.MessageType != EntityMessage.Null)
                    MessageBuffer.Enqueue(emsg);
            }
            else
            {
                ProcessMsgBuffer();
                var emsg = ProcessNetMessage(msg);
                if (!_entities.ContainsKey(emsg.Uid))
                    MessageBuffer.Enqueue(emsg);
                else
                    (_entities[emsg.Uid]).HandleNetworkMessage(emsg);
            }
        }


        #endregion

        /// <summary>
        /// Load all entities from SavedEntities.xml
        /// </summary>
        public void LoadEntities()
        {
            XElement tmp;
            try
            {
                tmp = XDocument.Load("SavedEntities.xml").Element("SavedEntities");
            }
            catch (FileNotFoundException e)
            {
                var saveFile = new XDocument(new XElement("SavedEntities"));
                saveFile.Save("SavedEntities.xml");
                tmp = XDocument.Load("SavedEntities.xml").Element("SavedEntities");
            }
            IEnumerable<XElement> SavedEntities = tmp.Descendants("SavedEntity");
            foreach (XElement e in SavedEntities)
            {
                LoadEntity(e);
            }
        }

        public void LoadEntity(XElement e)
        {
            float X = float.Parse(e.Attribute("X").Value, CultureInfo.InvariantCulture);
            float Y = float.Parse(e.Attribute("Y").Value, CultureInfo.InvariantCulture);

            Direction dir = Direction.South;
            if(e.Attribute("direction") != null)
                dir = (Direction)Enum.Parse(typeof(Direction), e.Attribute("direction").Value, true);

            string template = e.Attribute("template").Value;
            string name = e.Attribute("name").Value;
            Entity ent = SpawnEntity(template);
            ent.Name = name;
            ent.GetComponent<TransformComponent>(ComponentFamily.Transform).TranslateTo(new Vector2(X, Y));
            ent.GetComponent<DirectionComponent>(ComponentFamily.Direction).Direction = dir;
            ent.SendMessage(this, ComponentMessageType.WallMountSearch); //Tell wall mounted compos to look for a tile to attach to. I hate to do this here but i have to.
        }

        private XElement ToXML(Entity e)
        {
            var el = new XElement("SavedEntity",
                                  new XAttribute("X", e.GetComponent<TransformComponent>(ComponentFamily.Transform).Position.X.ToString(CultureInfo.InvariantCulture)),
                                  new XAttribute("Y", e.GetComponent<TransformComponent>(ComponentFamily.Transform).Position.Y.ToString(CultureInfo.InvariantCulture)),
                                  new XAttribute("template", e.Template.Name),
                                  new XAttribute("name", e.Name),
                                  new XAttribute("direction", e.GetComponent<DirectionComponent>(ComponentFamily.Direction).Direction.ToString()));
            return el;
        }

        /// <summary>
        /// Spawns an entity at a specific position
        /// </summary>
        /// <param name="EntityType"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public Entity SpawnEntityAt(string EntityType, Vector2 position, bool send = true)
        {
            Entity e = SpawnEntity(EntityType, false);
            e.GetComponent<TransformComponent>(ComponentFamily.Transform).TranslateTo(position);
            if (send) e.Initialize();
            //if (send) e.FireNetworkedSpawn();
            return e;
        }

        /// <summary>
        /// Disposes all entities and clears all lists.
        /// </summary>
        public void FlushEntities()
        {
            foreach (Entity e in _entities.Values)
                e.Shutdown();
            _entities.Clear();
        }

        public List<EntityState> GetEntityStates()
        {
            var stateEntities = new List<EntityState>();
            foreach(Entity entity in _entities.Values)
            {
                var entityState = entity.GetEntityState();
                stateEntities.Add(entityState);
            }
            return stateEntities;
        }

        public void Update(float frameTime)
        {
            _systemManager.Update(frameTime);
        }
    }
}