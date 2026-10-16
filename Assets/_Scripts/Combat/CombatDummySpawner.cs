using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Spawns <see cref="CombatDummy"/> players once the server is running, each set up to defend or attack differently,
    /// for testing blocking and parrying from both sides. Dummies are ordinary player ghosts that no client owns, so
    /// every client sees them and they can hit and be hit like players.
    /// Each dummy's settings can be changed live on the server's copy in the inspector.
    /// </summary>
    public class CombatDummySpawner : MonoBehaviour
    {
        [Serializable]
        public class DummySetup
        {
            public string Name = "Dummy";
            [Tooltip("Where to spawn. Empty uses Offset from the spawner.")]
            public Transform SpawnPoint;
            [Tooltip("Position relative to the spawner, used when there's no Spawn Point.")]
            public Vector3 Offset;
            public DummyDefense Defense = DummyDefense.Block;
            [Tooltip("Mixed defense: the chance of parrying each attack rather than blocking it.")]
            [Range(0f, 1f)] public float ParryChance = 0.5f;
            [Tooltip("Attack the nearest player with a combo string when in range.")]
            public bool Attack;
            [Tooltip("The buttons pressed for each combo string. Empty keeps the prefab's.")]
            public AttackKind[] AttackSequence = Array.Empty<AttackKind>();
            [Tooltip("Walk toward the nearest player.")]
            public bool FollowTarget;
        }

        [Tooltip("A player prefab variant with a CombatDummy. It must be in the NetworkManager's prefab list.")]
        public NetworkObject DummyPrefab;
        public List<DummySetup> Dummies = new()
        {
            new DummySetup { Name = "Dummy (Block)", Offset = new Vector3(-3f, 0f, 0f), Defense = DummyDefense.Block },
            new DummySetup { Name = "Dummy (Parry)", Offset = new Vector3(0f, 0f, 0f), Defense = DummyDefense.Parry },
            new DummySetup { Name = "Dummy (Attack)", Offset = new Vector3(3f, 0f, 0f), Defense = DummyDefense.None, Attack = true, FollowTarget = true },
        };

        readonly List<NetworkObject> m_Spawned = new();
        bool m_HasSpawned;

        /// <summary>The dummies spawned, on the server.</summary>
        public IReadOnlyList<NetworkObject> Spawned => m_Spawned;

        void Update()
        {
            if (m_HasSpawned || DummyPrefab == null) return;
            var networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer) return;

            m_HasSpawned = true;
            foreach (var setup in Dummies) Spawn(setup);
        }

        void Spawn(DummySetup setup)
        {
            var position = setup.SpawnPoint != null ? setup.SpawnPoint.position : transform.TransformPoint(setup.Offset);
            var rotation = setup.SpawnPoint != null ? setup.SpawnPoint.rotation : transform.rotation;
            var instance = Instantiate(DummyPrefab, position, rotation);
            instance.name = setup.Name;

            if (instance.TryGetComponent<CombatDummy>(out var dummy))
            {
                // The brain only runs on the server, so its settings don't need replicating.
                dummy.Defense = setup.Defense;
                dummy.ParryChance = setup.ParryChance;
                dummy.Attack = setup.Attack;
                dummy.FollowTarget = setup.FollowTarget;
                if (setup.AttackSequence != null && setup.AttackSequence.Length > 0) dummy.AttackSequence = setup.AttackSequence;
            }
            else
            {
                Debug.LogWarning($"[{nameof(CombatDummySpawner)}] {DummyPrefab.name} has no {nameof(CombatDummy)}, so it will stand still.", this);
            }

            // Spawned without an owner: no client drives it, and every client sees it as another player. The player is
            // owner predicted, which needs an owner id set; -1 is netcode's id for no player.
            instance.GetComponent<GhostObject>().OwnerNetworkId = new NetworkId { Value = -1 };
            instance.Spawn();
            m_Spawned.Add(instance);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
            foreach (var setup in Dummies)
            {
                var position = setup.SpawnPoint != null ? setup.SpawnPoint.position : transform.TransformPoint(setup.Offset);
                Gizmos.DrawWireSphere(position + Vector3.up, 0.4f);
                Gizmos.DrawLine(position, position + Vector3.up * 2f);
            }
        }
    }
}
