using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.LowLevel;
using Unity.Netcode.NetcodeTime;

namespace Unity.Netcode
{
    /// <summary>
    /// Matches each projectile the server spawns to the shooter's predicted copy (see <see cref="Projectile"/>).
    /// The default classification only compares ghost type and spawn tick, so another player's fireball fired within
    /// the tick window could replace yours. This one also needs the same <see cref="Projectile.LaunchShooterGhostId"/>,
    /// and takes the copy with the closest spawn tick. It compares the launch shooter rather than the current one,
    /// because a parry changes the current shooter and either side may have bounced the projectile before the server's
    /// first snapshot of it arrives. A player's fireballs are a Special cooldown apart, far more than the
    /// window, so at most one copy can match.
    /// It claims every projectile spawn, matched or not, so the default classification never sees them. A copy that
    /// isn't matched is destroyed as usual once the extra lifetime runs out.
    /// The tick window is the ClientTickRate's DefaultClassificationAllowableTickPeriod, the same as the default.
    /// Clients only: the single-world host has no predicted copies to match.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
    [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
    [CreateAfter(typeof(GhostCollectionSystem))]
    [CreateAfter(typeof(GhostReceiveSystem))]
    [BurstCompile]
    public partial struct ProjectileSpawnClassificationSystem : ISystem
    {
        SnapshotDataLookupHelper m_SnapshotLookupHelper;
        BufferLookup<PredictedGhostSpawn> m_PredictedSpawnLookup;
        ComponentLookup<Projectile.Projectile_LaunchShooterGhostId_Gen> m_LaunchShooterLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_SnapshotLookupHelper = new SnapshotDataLookupHelper(ref state,
                SystemAPI.GetSingletonEntity<GhostCollection>(),
                SystemAPI.GetSingletonEntity<SpawnedGhostEntityMap>());
            m_PredictedSpawnLookup = state.GetBufferLookup<PredictedGhostSpawn>();
            m_LaunchShooterLookup = state.GetComponentLookup<Projectile.Projectile_LaunchShooterGhostId_Gen>(true);
            state.RequireForUpdate<GhostSpawnQueue>();
            state.RequireForUpdate<PredictedGhostSpawnList>();
            state.RequireForUpdate<GhostCollection>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_SnapshotLookupHelper.Update(ref state);
            m_PredictedSpawnLookup.Update(ref state);
            m_LaunchShooterLookup.Update(ref state);
            if (!SystemAPI.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            int acceptedTickPeriod = (int)clientTickRate.DefaultClassificationAllowableTickPeriod;

            // Runs on the main thread: an IJobEntity can't use the GhostField's generated component type
            // (error SGJE0024), and creating the snapshot lookup completes the jobs it reads anyway.
            state.CompleteDependency();
            var snapshotLookup = m_SnapshotLookupHelper.CreateSnapshotBufferLookup();
            var queueEntity = SystemAPI.GetSingletonEntity<GhostSpawnQueue>();
            var ghosts = state.EntityManager.GetBuffer<GhostSpawnBuffer>(queueEntity);
            var data = state.EntityManager.GetBuffer<SnapshotDataBuffer>(queueEntity, true);
            var spawnList = m_PredictedSpawnLookup[SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>()];

            for (int i = 0; i < ghosts.Length; ++i)
            {
                ref var ghost = ref ghosts.ElementAt(i);
                if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted || ghost.HasClassifiedPredictedSpawn || ghost.PredictedSpawnEntity != Entity.Null)
                    continue;
                // Only projectiles have this component in their snapshot.
                if (!snapshotLookup.TryGetComponentDataFromSpawnBuffer(ghost, data, out Projectile.Projectile_LaunchShooterGhostId_Gen launchShooter))
                    continue;
                ghost.HasClassifiedPredictedSpawn = true;

                int best = -1;
                int bestGap = acceptedTickPeriod;
                for (int j = 0; j < spawnList.Length; ++j)
                {
                    ref readonly var predicted = ref spawnList.ElementAt(j);
                    if (predicted.ghostType != ghost.GhostType ||
                        !m_LaunchShooterLookup.TryGetComponent(predicted.entity, out var predictedShooter) ||
                        predictedShooter.m_Value != launchShooter.m_Value)
                        continue;
                    int gap = math.abs(ghost.ServerSpawnTick.TicksSince(predicted.spawnTick));
                    if (gap < bestGap)
                    {
                        best = j;
                        bestGap = gap;
                    }
                }
                if (best >= 0)
                {
                    ghost.PredictedSpawnEntity = spawnList[best].entity;
                    spawnList.RemoveAtSwapBack(best);
                }
            }
        }
    }
}
