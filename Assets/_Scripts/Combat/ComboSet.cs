using System.Collections.Generic;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A character's move list: the attack each button starts from neutral. Each <see cref="AttackData"/> lists its own
    /// follow-ups, so the set is a tree (or graph) of attacks.
    /// Every attack reachable from the starters gets an index, which is how the current attack is replicated. The
    /// order is worked out the same way on every machine, so the server and clients agree on it.
    /// Create with Assets > Create > Combat > Combo Set and assign it to <see cref="CombatControllerGhost.Combos"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Combat/Combo Set", fileName = "ComboSet")]
    public class ComboSet : ScriptableObject
    {
        [Tooltip("The attack each button starts when not already attacking.")]
        public List<ComboLink> Starters = new();

        List<AttackData> m_Attacks;
        Dictionary<AttackData, int> m_Indices;

        /// <summary>Every attack reachable from <see cref="Starters"/>, breadth first.</summary>
        public IReadOnlyList<AttackData> Attacks
        {
            get
            {
                if (m_Attacks == null) Build();
                return m_Attacks;
            }
        }

        /// <summary>The attack that <paramref name="input"/> starts from neutral, or null.</summary>
        public AttackData GetStarter(AttackKind input)
        {
            foreach (var link in Starters)
                if (link.Input == input && link.Attack != null) return link.Attack;
            return null;
        }

        /// <summary>The attack's id: 1 + its index in <see cref="Attacks"/>, or 0 if it isn't in this set.</summary>
        public int GetId(AttackData attack)
        {
            if (m_Attacks == null) Build();
            return attack != null && m_Indices.TryGetValue(attack, out var index) ? index + 1 : 0;
        }

        /// <summary>The attack with the given id from <see cref="GetId"/>, or null.</summary>
        public AttackData GetAttack(int id)
        {
            var attacks = Attacks;
            return id > 0 && id <= attacks.Count ? attacks[id - 1] : null;
        }

        void Build()
        {
            m_Attacks = new List<AttackData>();
            m_Indices = new Dictionary<AttackData, int>();
            var queue = new Queue<AttackData>();
            foreach (var link in Starters) Visit(link.Attack, queue);
            while (queue.Count > 0)
            {
                foreach (var link in queue.Dequeue().FollowUps) Visit(link.Attack, queue);
            }
        }

        void Visit(AttackData attack, Queue<AttackData> queue)
        {
            if (attack == null || m_Indices.ContainsKey(attack)) return;
            m_Indices[attack] = m_Attacks.Count;
            m_Attacks.Add(attack);
            queue.Enqueue(attack);
        }

        // Follow-ups can be edited at any time; rebuild the index on next use.
        void OnValidate() => m_Attacks = null;
        void OnEnable() => m_Attacks = null;
    }
}
