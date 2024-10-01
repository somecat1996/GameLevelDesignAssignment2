using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Unity.Collections;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;
using Unity.Jobs;
using Cinemachine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DevDev.LDP
{
    public static partial class Procs
    {
        #region // Initialization
        static Simulation_Environment
        SimEnvironment_GetInSceneAndInitialize(
              Scene scene
            , GameParams gameParams
            )
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            Simulation_Environment_mono envMono;
            foreach (GameObject go in rootObjects)
            {
                envMono = go.GetComponent<Simulation_Environment_mono>();
                if (envMono != null)
                {
                    Simulation_Environment env = envMono.data;
                    env.name = scene.name;
                    env.rootGameObject = envMono.gameObject;
                    env.rootTransform = envMono.transform;
                    env.scene = scene;
                    // Find collectables
                    {
                        Collectable_mono[] collectables_mono = env.rootGameObject.GetComponentsInChildren<Collectable_mono>();
                        env.collectables = new Collectable[collectables_mono.Length];
                        Collectable coll;
                        for (int i = 0; i < collectables_mono.Length; i++)
                        {
                            coll = collectables_mono[i].data;
                            // NOTE(stef): For now we assume only one sensor on collectable animator
                            coll.animationCompletionSensor = coll.animator.GetBehaviour<AnimStateEntrySensor>();
                            coll.animID_collected.InitializeID();
                            env.collectables[i] = coll;
                        }
                    }
                    // Find exits
                    {
                        LevelExit_mono[] exits_mono = env.rootGameObject.GetComponentsInChildren<LevelExit_mono>();
                        env.exits = new LevelExit[exits_mono.Length];
                        for (int i = 0; i < exits_mono.Length; i++)
                        {
                            env.exits[i] = exits_mono[i].data;
                        }
                    }

                    // find hazards
                    {
                        Hazard_mono[] hazards_mono = env.rootGameObject.GetComponentsInChildren<Hazard_mono>();
                        env.hazards = new Hazard[hazards_mono.Length];
                        for (int i = 0; i < hazards_mono.Length; i++)
                        {
                            env.hazards[i] = hazards_mono[i].data;
                        }
                    }

                    // find moving platforms
                    {
                        MovingPlatform_mono[] monos = env.rootGameObject.GetComponentsInChildren<MovingPlatform_mono>();
                        env.movingPlatforms = new MovingPlatform[monos.Length];
                        for (int i = 0; i < monos.Length; i++)
                        {
                            env.movingPlatforms[i] = monos[i].data;
                        }
                    }

                    // find checkpoints
                    {
                        Checkpoint_mono[] checkpoints_mono = env.rootGameObject.GetComponentsInChildren<Checkpoint_mono>();
                        env.checkpoints = new Checkpoint[checkpoints_mono.Length];
                        for (int i = 0; i < checkpoints_mono.Length; i++)
                        {
                            env.checkpoints[i] = checkpoints_mono[i].data;
                            env.checkpoints[i].animID_isActive.InitializeID();
                            env.checkpoints[i].valid = true;
                        }
                    }

                    // find enemies
                    {
                        Enemy_mono[] enemies_mono = env.rootGameObject.GetComponentsInChildren<Enemy_mono>();
                        env.enemies = new Enemy[enemies_mono.Length];
                        for (int i = 0; i < enemies_mono.Length; i++)
                        {
                            env.enemies[i] = enemies_mono[i].data;
                            InitializeEnemy(ref env.enemies[i]);
                        }
                    }
                    return env;
                }
            }
            return null;
        }

        static void
        InitializePlayerAvatar(ref PlayerAvatar player)
        {
            player.finishedSpawning = false;
            InitializePawn(ref player.pawn);
        }

        static void
        InitializeEnemy(ref Enemy enemy)
        {
            enemy.valid = true;
            enemy.animParams.animParam_angry.InitializeID();
            InitializePawn(ref enemy.pawn);
        }

        static void
        InitializePawn(ref Pawn pawn)
        {
            pawn.colliderID = pawn.physicsCollider.GetInstanceID();
            pawn.characterHeight = pawn.top.localPosition.y - pawn.bottom.localPosition.y;
            pawn.hipsHeight = pawn.hips.localPosition.y - pawn.bottom.localPosition.y;
            pawn.colliderID = pawn.physicsCollider.GetInstanceID();

            pawn.animParams.animParam_grounded.InitializeID();
            pawn.animParams.animParam_isJumping.InitializeID();
            pawn.animParams.animParam_jumpStart.InitializeID();
            pawn.animParams.animParam_killed.InitializeID();
            pawn.animParams.animParam_moveSpeed.InitializeID();

            // Reset animation incase it's already playing, which may be the case if it was already in the level on load and wasn't instantiated
            // NOTE(stef): this needs to be done before linking the animation state behaviours - they get reset when the animator does.
            pawn.rootTransform.gameObject.SetActive(false);
            pawn.rootTransform.gameObject.SetActive(true);

            // Get anim state sensors
            {
                pawn.animParams.animSensor_appearFinished.InitializeID();
                pawn.animParams.animSensor_disappearFinished.InitializeID();

                // NOTE(stef): must activate gameobject before querying animator for behaviours
                pawn.rootTransform.gameObject.SetActive(true);
                AnimStateEntrySensor[] animSensors = pawn.animator.GetBehaviours<AnimStateEntrySensor>();
                int sensorNameHash;
                for (int i = 0; i < animSensors.Length; i++)
                {
                    sensorNameHash = Animator.StringToHash(animSensors[i].sensorName);
                    if (sensorNameHash == pawn.animParams.animSensor_appearFinished.nameHash)
                    {
                        pawn.animSensor_spawnFinished = animSensors[i];
                        pawn.animSensor_spawnFinished.stateEntered = false;
                    }
                    else if (sensorNameHash == pawn.animParams.animSensor_disappearFinished.nameHash)
                    {
                        pawn.animSensor_deathFinished = animSensors[i];
                        pawn.animSensor_deathFinished.stateEntered = false;
                    }
                }
            }

            // pawn is not simulated until spawn animation is complete
            pawn.rigidbody.simulated = false;

            pawn.valid = true;
        }
        #endregion

        static void
        RecallLevelState(ref Simulation sim, LevelSaveState saveState)
        {
            // Enemies
            // TODO(stef): make this more robust. Include animator state. Set animation variables.
            //             This will become simpler if/when we store more state on our own in the pawn struct (as opposed to relying on Transform of position and rigidbody for velocity)
            saveState.enemies.CopyTo(sim.environment.enemies, 0);
            Enemy enemy;
            for (int e = 0; e < sim.environment.enemies.Length; e++)
            {
                enemy = sim.environment.enemies[e];
                if (enemy.valid)
                {
                    // TODO(stef): not all functionality in itialize pawn is needed, consider extracting
                    InitializeEnemy(ref enemy);
                    enemy.pawn.rootTransform.position = (Vector3)(Vector2)saveState.enemies_position[e];
                    enemy.pawn.rigidbody.velocity = saveState.enemies_velocity[e];
                    sim.environment.enemies[e] = enemy;
                }
            }

            //sim.elapsedSeconds = saveState.elapsedSeconds;

            // Pickups
            sim.pickupCount = saveState.playerCollectableCount;
            Collectable coll;
            bool wasCollected;
            for (int p = 0; p < sim.environment.collectables.Length; p++)
            {
                coll = sim.environment.collectables[p];
                wasCollected = saveState.pickups_collected[p];
                if (!wasCollected)
                {
                    coll.rootGameObject.SetActive(true);
                    // NOTE(stef): Apparently connection to animation behaviours is lost when objects are disabled, so we need to do this every time we re-enable collectables                            
                    coll.animationCompletionSensor = coll.animator.GetBehaviour<AnimStateEntrySensor>();
                    coll.animator.Play(0);
                    coll.collected = false;
                    sim.environment.collectables[p] = coll;
                }
            }

            // platforms
            saveState.platforms.CopyTo(sim.environment.movingPlatforms, 0);
        }

        static void
        SaveLevelState(
                ref LevelSaveState levelSaveState
            , Simulation sim
            , int checkpointIndex)
        {
            levelSaveState.checkpointIndex = checkpointIndex;

            // enemies
            levelSaveState.enemies = new Enemy[sim.environment.enemies.Length];
            sim.environment.enemies.CopyTo(levelSaveState.enemies, 0);
            levelSaveState.enemies_position = new float2[sim.environment.enemies.Length];
            levelSaveState.enemies_velocity = new float2[sim.environment.enemies.Length];
            Enemy enemy;
            for (int e = 0; e < levelSaveState.enemies.Length; e++)
            {
                enemy = levelSaveState.enemies[e];
                if (enemy.valid)
                {
                    levelSaveState.enemies_position[e] = ((float3)enemy.pawn.rootTransform.position).xy;
                    levelSaveState.enemies_velocity[e] = enemy.pawn.rigidbody.velocity;
                }
            }

            levelSaveState.elapsedSeconds = sim.elapsedSeconds;

            // Collectables
            levelSaveState.playerCollectableCount = sim.pickupCount;
            levelSaveState.pickups_collected = new bool[sim.environment.collectables.Length];
            for (int p = 0; p < sim.environment.collectables.Length; p++)
            {
                levelSaveState.pickups_collected[p] = sim.environment.collectables[p].collected;
            }
            levelSaveState.valid = true;

            // moving platforms
            levelSaveState.platforms = new MovingPlatform[sim.environment.movingPlatforms.Length];
            sim.environment.movingPlatforms.CopyTo(levelSaveState.platforms, 0);
        }

        internal static void
        TickGameplaySim(
              ref Simulation sim
            , float deltaTime
            , Input.Sample_Pawn playerInput
            , AssetReferences assetRefs
            )
        {
            bool isFirstTick = !sim.simmedOnce;
            sim.simmedOnce = true;

            if (isFirstTick)
            {
                SaveLevelState(ref sim.levelSaveState, sim, checkpointIndex: -1);
            }

            #region // Spawn / re-spawn player
            if (!sim.playerAvatar.pawn.valid)
            {
                Vector3 spawnPosition;
                {
                    if (sim.levelSaveState.checkpointIndex >= 0)
                        spawnPosition = sim.environment.checkpoints[sim.levelSaveState.checkpointIndex].spawnPoint.position;
                    else
                        spawnPosition = sim.environment.playerSpawn.position;

                    RecallLevelState(ref sim, sim.levelSaveState);
                }

                PlayerAvatar_mono player_mono = GameObject.FindObjectOfType<PlayerAvatar_mono>();
                if (player_mono == null)
                {
                    player_mono = GameObject.Instantiate(assetRefs.playerFab);
                    player_mono.transform.position = spawnPosition;
                }

#if !UNITY_EDITOR
                player_mono.transform.position = spawnPosition;
#endif

                player_mono.transform.SetParent(sim.environment.rootTransform, true);
                sim.environment.cam_followCam.Follow = player_mono.transform;
                sim.environment.cam_followCam.LookAt = player_mono.transform;
                if (!isFirstTick)
                    sim.environment.cam_followCam.OnTargetObjectWarped(player_mono.transform, player_mono.transform.position - sim.lastKnownPlayerPos);
                //sim.environment.virtualCamera_followCam.OnTargetObjectWarped;
                sim.playerAvatar = player_mono.data;
                InitializePlayerAvatar(ref sim.playerAvatar);
                sim.playerAvatar.pawn.as_spawn.Play();
            }
            #endregion

            #region // Moving platforms
            {
                for (int i = 0; i < sim.environment.movingPlatforms.Length; i++)
                {
                    MovingPlatform plat = sim.environment.movingPlatforms[i];
                    plat.progress = (plat.progress + (deltaTime * (1f / plat.cycleTime))) % 1f;
                    float3 newPos = MovingPlatform_EvaluatePosition(plat, plat.loopOffset + plat.progress);
                    plat.lastKnownVelocity = (newPos.xy - ((float3)plat.platform.position).xy) / deltaTime;
                    plat.platform.position = newPos;
                    sim.environment.movingPlatforms[i] = plat;
                }
            }
            #endregion

            GlobalDebug.gizmos_footholds.nCommands = 0;

            #region // Enemy sim
            for (int i = 0; i < sim.environment.enemies.Length; i++)
            {
                Enemy en = sim.environment.enemies[i];
                if (en.valid)
                {
                    if (!en.pawn.killed)
                    {
                        Enemy_Params aiParams = en.aiParams.data;

                        bool canSeePlayer = false;
                        float2 moveDir = new float2(en.movingRight ? 1 : -1, 0);
                        float2 toPlayer = 0;
                        if (sim.playerAvatar.pawn.valid)
                        {
                            if (en.pawn.physicsCollider.IsTouching(sim.playerAvatar.pawn.physicsCollider))
                            {
                                en.as_hitPlayer.Play();
                                KillPawn(ref sim.playerAvatar.pawn);
                                //en.alerted = false;
                            }

                            toPlayer = (float2)(Vector2)(sim.playerAvatar.pawn.rootTransform.position - en.pawn.rootTransform.position);
                            float visionDistance = en.isAggro ? aiParams.vision_maxDist_aggro : aiParams.vision_maxDist_calm;

                            // player must be close enough and near the same y-position
                            canSeePlayer = math.length(toPlayer) <= visionDistance
                                            && math.abs(toPlayer.y) <= en.pawn.characterHeight * .4f;

                            // must be facing player to see the player if not already aggro'd
                            if (!en.isAggro)
                                canSeePlayer &= sign(toPlayer.x) == sign(moveDir.x);

                            // also check for occlusion, there must be nothing blocking the enemy's vision
                            if (canSeePlayer)
                            {
                                /// TODO(stef) probably just use layers for filtering instead of checking for player collider explicitly
                                int nhits = Physics2D.RaycastNonAlloc(en.pawn.rootTransform.position, toPlayer, sim.cache_raycastHits, math.length(toPlayer));
                                RaycastHit2D hit;
                                for (int h = 0; h < nhits; h++)
                                {
                                    hit = sim.cache_raycastHits[h];
                                    if (hit.collider.isTrigger || hit.collider == en.pawn.physicsCollider)
                                        continue;

                                    canSeePlayer &= hit.collider == sim.playerAvatar.pawn.physicsCollider;
                                    break;
                                }
                            }

                        }

                        // Track time since the enemy has seen the player
                        if (canSeePlayer)
                            en.timeSinceSeenPlayer = 0;
                        else
                            en.timeSinceSeenPlayer += deltaTime;

                        // enter aggro state if we see the player
                        if (!en.isAggro && canSeePlayer)
                        {
                            en.isAggro = true;
                            en.pawn.animator.SetBool(en.animParams.animParam_angry.nameHash, en.isAggro);
                        }
                        // exit aggro state if we havent seen the player for ong enough
                        else if (en.isAggro && en.timeSinceSeenPlayer >= aiParams.aggroLossDelay)
                        {
                            en.isAggro = false;
                            en.pawn.animator.SetBool(en.animParams.animParam_angry.nameHash, en.isAggro);
                        }

                        // move toward player if we can see the player
                        if (en.isAggro && canSeePlayer)
                        {
                            en.movingRight = toPlayer.x > 0;
                            moveDir.x = en.movingRight ? 1 : -1;
                        }

                        // Modify input magnitude depending on aggro state
                        moveDir.x *= en.isAggro ? aiParams.speedScale_aggro : aiParams.speedScale_calm;

                        Input.Sample_Pawn enemyInput = new Input.Sample_Pawn
                        {
                            moveDir = moveDir.x,
                        };

                        SimPawnKinematics(
                            ref en.pawn
                            , enemyInput
                            , en.pawn.kinematicsParams.data
                            , deltaTime
                            , sim.environment
                            , sim.environment.enemies
                            , sim.cache_raycastHits
                            , out Foothold foothold_near
                            , out Foothold foothold_forward
                            );


                        // change direction if we can't move forward any further
                        if (!foothold_forward.valid || foothold_forward.forwardDistanceNormalized < 1f)
                        {
                            en.movingRight = !en.movingRight;
                        }
                        sim.environment.enemies[i] = en;

                    }
                    // Remove killed enemies once their death animation is complete
                    else if (en.pawn.animSensor_deathFinished.stateEntered)
                    {
                        en.valid = false;
                        en.pawn.valid = false;
                        en.pawn.rootTransform.gameObject.SetActive(false);
                    }
                }
            }
            #endregion

            #region // player kinematics
            if (!sim.playerAvatar.finishedSpawning && sim.playerAvatar.pawn.animSensor_spawnFinished.ConsumeFlag())
            {
                sim.playerAvatar.finishedSpawning = true;
            }

            if (sim.playerAvatar.pawn.valid && sim.playerAvatar.finishedSpawning && !sim.playerAvatar.pawn.killed)
            {
                // Player kinematics
                SimPawnKinematics(
                    ref sim.playerAvatar.pawn
                    , playerInput
                    , sim.playerAvatar.pawn.kinematicsParams.data
                    , deltaTime
                    , sim.environment
                    , sim.environment.enemies
                    , sim.cache_raycastHits
                    , out Foothold foothold_near
                    , out Foothold foothold_forward
                    );
            }
            #endregion 
            Physics2D.Simulate(deltaTime);

            #region // Player sim - world interaction
            {
                Pawn pawn = sim.playerAvatar.pawn;
                if (pawn.valid && !pawn.killed)
                {
                    // pickups
                    {
                        Collectable coll;
                        for (int i = 0; i < sim.environment.collectables.Length; i++)
                        {
                            coll = sim.environment.collectables[i];
                            if (!coll.collected && pawn.hitbox.IsTouching(coll.collider))
                            {
                                coll.collected = true;
                                coll.animator.SetTrigger(coll.animID_collected.nameHash);
                                coll.as_collected.Play();
                                sim.pickupCount++;
                                sim.environment.collectables[i] = coll;
                            }
                        }
                    }

                    // hazards
                    for (int i = 0; i < sim.environment.hazards.Length; i++)
                    {
                        if (pawn.hitbox.IsTouching(sim.environment.hazards[i].collider))
                        {
                            sim.environment.hazards[i].hitSound.Play();
                            KillPawn(ref pawn);
                        }
                    }

                    // checkpoints
                    Checkpoint checkpoint;
                    for (int cp = 0; cp < sim.environment.checkpoints.Length; cp++)
                    {
                        if (cp != sim.levelSaveState.checkpointIndex)
                        {
                            checkpoint = sim.environment.checkpoints[cp];
                            if (pawn.hitbox.IsTouching(checkpoint.collider))
                            {
                                // Set checkpoint
                                checkpoint.animator.SetBool(checkpoint.animID_isActive.nameHash, true);
                                checkpoint.as_activate.Play();
                                SaveLevelState(ref sim.levelSaveState, sim, cp);

                                // Set all other checkpoints to not hit
                                for (int ocp = 0; ocp < sim.environment.checkpoints.Length; ocp++)
                                {
                                    if (ocp != cp)
                                        sim.environment.checkpoints[ocp].animator.SetBool(sim.environment.checkpoints[ocp].animID_isActive.nameHash, false);
                                }
                                break;
                            }
                        }
                    }

                    // Level exit / complete
                    if (!sim.levelComplete)
                    {
                        for (int i = 0; i < sim.environment.exits.Length; i++)
                        {
                            if (pawn.hitbox.IsTouching(sim.environment.exits[i].collider))
                            {
                                sim.levelComplete = true;
                                SetSimPaused(ref sim, true);
                                sim.environment.exits[i].as_activate.Play();
                            }
                        }
                    }

                    // kill floor
                    if (pawn.rigidbody.position.y < sim.environment.boundary_killFloor.position.y)
                    {
                        KillPawn(ref pawn);
                    }

                    sim.playerAvatar.pawn = pawn;
                }
            }
            #endregion // Player sim - world interaction

            #region // Disable collectables if they've been collected and their disappear animation has finished
            {
                Collectable coll;
                for (int i = 0; i < sim.environment.collectables.Length; i++)
                {
                    coll = sim.environment.collectables[i];
                    if (coll.collected && coll.rootGameObject.activeInHierarchy && coll.animationCompletionSensor.stateEntered)
                    {
                        coll.rootGameObject.SetActive(false);
                        coll.animator.StopPlayback();
                        coll.animationCompletionSensor.stateEntered = false;
                    }
                }
            }
            #endregion

            #region // Remove killed player
            if (sim.playerAvatar.pawn.valid && sim.playerAvatar.pawn.killed && sim.playerAvatar.pawn.animSensor_deathFinished.stateEntered)
            {
                sim.playerAvatar.pawn.valid = false;
                sim.lastKnownPlayerPos = sim.playerAvatar.pawn.rigidbody.position;
                GameObject.DestroyImmediate(sim.playerAvatar.pawn.rootTransform.gameObject);
            }
            #endregion

            #region // remove killed enemies

            #endregion

            sim.elapsedSeconds += deltaTime;
        }

        public static float3
        MovingPlatform_EvaluatePosition(
              MovingPlatform platform
            , float progress
            )
        {
            switch (platform.trackType)
            {
                case PlatformTrackType.CIRCULAR:
                    {
                        float theta = progress * PI * 2;
                        float3 offsetDir = 0;
                        offsetDir.x = cos(theta);
                        offsetDir.y = sin(theta);
                        return platform.rootTransform.position + (Vector3)offsetDir * platform.circular_radius;
                    }
                case PlatformTrackType.PINGPONG:
                    {
                        return lerp(platform.pingpong_waypoint_0.position, platform.pingpong_waypoint_1.position, Mathf.PingPong(progress * 2, 1));
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        static void KillPawn(ref Pawn pawn)
        {
            pawn.killed = true;
            pawn.rigidbody.simulated = false;
            pawn.animator.SetTrigger(pawn.animParams.animParam_killed.nameHash);
            pawn.as_death.Play();
        }

        #region // Kinematics
        static void
        SimPawnKinematics(
              ref Pawn pawn
            , Input.Sample_Pawn pawnInput
            , PawnKinematicsParams kinematicsParams
            , float deltaTime
            , Simulation_Environment environment
            , Enemy[] enemies
            , RaycastHit2D[] cache_raycastHits
            , out Foothold foothold_near
            , out Foothold foothold_forward
            )
        {
            // TODO(stef): Extract functionality that does not belong to all pawns. For exemple, enemies don't jump so this code doesn't need to be here

            // pawn is not simulated until spawn animation is complete.
            // TODO(stef): Do this only once after spawn animation is complete, not that it really matters.
            pawn.rigidbody.simulated = true;

            float2 hipsPosition = ((float3)pawn.hips.position).xy;
            float2 gravity = Physics2D.gravity * pawn.rigidbody.gravityScale;
            float2 gravityDir = normalize(gravity);
            float2 right = new float2(-gravityDir.y, gravityDir.x);
            float2 forwardDir = right * (pawnInput.moveDir < 0 ? -1 : 1);
            float2 velocity_last = pawn.rigidbody.velocity;
            float moveInputMagnitude = abs(pawnInput.moveDir);
            TransformEnergy spatialEnergy = default;

            int footholdLayerMask = ~0;
            if (pawnInput.fallthrough)
                footholdLayerMask &= ~(1 << Defines.layer_oneWayPlatforms.value);

            #region // get footholds
            // NOTE(stef): near foothold is the nearest available place the character can step
            //             forward foothold is the furthest forward available place the character can step
            //             in the case that the only available footholds are behind the character, only the near foothold is found
            foothold_near = default;
            foothold_forward = default;
            float halfWidth = kinematicsParams.legReach_pawnWidth * .5f;

            // NOTE(stef): splitting the raycasts into 2 separate loops allows us to ensure we have a ray at the both extents of the range AND exactly below the pawn
            //             this could probably be made simpler
            // search behind
            RaycastRangeForFootholds(
                  ref foothold_near
                , ref foothold_forward
                , pawn
                , kinematicsParams
                , hipsPosition
                , forwardDir
                , gravityDir
                , rangeBehind: halfWidth
                , rangeAhead: 0
                , skipFirstRay: false
                , footholdLayerMask
                , cache_raycastHits
                );

            // search ahead
            RaycastRangeForFootholds(
                  ref foothold_near
                , ref foothold_forward
                , pawn
                , kinematicsParams
                , hipsPosition
                , forwardDir
                , gravityDir
                , rangeBehind: 0
                , rangeAhead: halfWidth + (kinematicsParams.legReach_extraWhenRunning * abs(pawnInput.moveDir))
                , skipFirstRay: true
                , footholdLayerMask
                , cache_raycastHits
                );

            GizmosSetColor(ref GlobalDebug.gizmos_footholds, Colors.coral);
            if (foothold_near.valid)
                GizmosDrawWireCube(ref GlobalDebug.gizmos_footholds, foothold_near.position, .1f);
            if (foothold_forward.valid)
                GizmosDrawCube(ref GlobalDebug.gizmos_footholds, foothold_forward.position, .09f);

            Foothold foothold_primary;
            if (foothold_forward.valid)
                foothold_primary = foothold_forward;
            else if (foothold_near.valid)
                foothold_primary = foothold_near;
            else
                foothold_primary = default;
            #endregion

            float2 groundVelocity = 0;
            #region // Determine ground velocity
            if (foothold_primary.valid)
            {
                MovingPlatform plat;
                for (int i = 0; i < environment.movingPlatforms.Length; i++)
                {
                    plat = environment.movingPlatforms[i];
                    if (plat.platformCollider != null && plat.platformCollider.GetInstanceID() == foothold_primary.colliderID)
                    {
                        groundVelocity = plat.lastKnownVelocity;
                        break;
                    }
                }
            }
            #endregion

            float verticalGroundSpeed = dot(groundVelocity, -gravityDir);
            //float2 velocityTransferFromMovingGround = groundVeloctiy - max(, 0);

            bool jumpStarted = false;
            float verticalSpeed = math.dot(velocity_last, -gravityDir);
            bool anyDownwardGrip = verticalSpeed + (-length(gravity) * deltaTime) < verticalGroundSpeed;
            bool onGround = anyDownwardGrip && foothold_near.valid && foothold_near.rayHitDistance <= pawn.hipsHeight;

            // no longer jumping when we are no longer traveling upwards
            // NOTE(stef): Must be before checking for bounce start or we could end up allowing bounce and jump on the same frame
            if (pawn.isJumping && math.dot(velocity_last, gravityDir) >= 0)
                pawn.isJumping = false;

            #region // Bouncing
            // Detect if we are standing on any enemies
            if (onGround)
            {
                Enemy enemy;
                for (int i = 0; i < enemies.Length; i++)
                {
                    enemy = enemies[i];
                    if (enemy.valid && !enemy.pawn.killed && foothold_primary.colliderID == enemy.pawn.colliderID)
                    {
                        pawn.isJumping = true;
                        jumpStarted = true;
                        KillPawn(ref enemies[i].pawn);

                        spatialEnergy.acceleration_impulse += math.sqrt(-gravity * 2 * kinematicsParams.bounceHeight);
                        break;
                    }
                }
            }
            #endregion

            #region // Jumping
            if (pawnInput.jumpTriggered)
                pawn.timeSinceJumpTriggered = 0;
            else
                pawn.timeSinceJumpTriggered += deltaTime;

            if (onGround)
                pawn.timeSinceGrounded = 0;
            else
                pawn.timeSinceGrounded += deltaTime;

            if (!pawn.isJumping
                && pawnInput.jumpValue == true
                && pawn.timeSinceJumpTriggered <= kinematicsParams.jumpForgiveness_early
                && (pawn.timeSinceGrounded <= kinematicsParams.jumpForgiveness_late))
            {
                pawn.isJumping = true;
                jumpStarted = true;
                spatialEnergy.acceleration_impulse += math.sqrt(-gravity * 2 * kinematicsParams.jumpHeight);
            }
            #endregion

            // NOTE(stef): Our run speed is currently limited in 2 axis, so we should have the same 2D speed regardless of slope. We may in the future want to be limited on the x-axis only, which would allow faster 2D speeds on slopes, but consistent x velocity
            // Limit expression of forward intent to amount of traction we have. (Simplified: we need at least some energy pushing us down)
            float2 desiredDownwardVelocity = groundVelocity;
            if (!pawn.isJumping && foothold_primary.valid && anyDownwardGrip)
            {
                #region // Intentional movement energy - ground
                if (moveInputMagnitude > float.Epsilon)
                {
                    float2 moveIntent_dir;
                    if (foothold_forward.valid)
                    {
                        if (foothold_near.valid && foothold_near.forwardDistance != foothold_primary.forwardDistance)
                            moveIntent_dir = normalizesafe(foothold_forward.position - foothold_near.position);
                        else
                            moveIntent_dir = normalizesafe(foothold_forward.position - (float2)(Vector2)pawn.bottom.position);
                    }
                    else
                    {
                        moveIntent_dir = forwardDir;
                    }
                    UnityEngine.Debug.DrawRay(new float3(hipsPosition, 0), new float3(moveIntent_dir, 0), Color.yellow);

                    float2 targetVelocity = groundVelocity + (moveIntent_dir * moveInputMagnitude * kinematicsParams.maxRunSpeed);
                    desiredDownwardVelocity = gravityDir * math.dot(targetVelocity, gravityDir);
                    float2 currentToTarget = targetVelocity - velocity_last;
                    // Acceleration needed to get us to the target velocity
                    float2 accelToTarget = currentToTarget * (1f / deltaTime);
                    float2 accel = math.normalizesafe(accelToTarget) * math.min(math.length(accelToTarget), kinematicsParams.maxRunAccel);
                    // Only contribute more energy that is positive in the forward and up axis. Let drag do the work of slowing down, and let gravity do the work of bringing us downward
                    accel = (forwardDir * math.dot(accel, forwardDir)) + (-gravityDir * math.dot(accel, -gravityDir));
                    spatialEnergy.acceleration += accel;
                }
                #endregion

                #region // Lateral ground drag (passive and active)
                {
                    float lateralVelocityRelative = math.dot(velocity_last - groundVelocity, right);

                    float passive = moveInputMagnitude == 0 ? kinematicsParams.drag_braking_passive : 0;
                    float active = kinematicsParams.drag_braking_reversalHelp * (sign(velocity_last.x) != sign(pawnInput.moveDir) ? moveInputMagnitude : 0);
                    float braking = math.max(passive, active);
                    if (math.abs(lateralVelocityRelative) > float.Epsilon)
                    {
                        float momentum = math.abs(lateralVelocityRelative) * (1f / deltaTime) * pawn.rigidbody.mass;
                        // brake force is clamped to be no more than the current momentum to avoid using more force than necessary and sending us backwards
                        float brakeForce = math.min(braking, momentum);
                        spatialEnergy.force += (right * -sign(lateralVelocityRelative) * brakeForce);
                    }
                }
                #endregion
            }

            #region // Intentional movement energy - air control
            if (!foothold_primary.valid)
            {
                float2 targetVelocity = forwardDir * moveInputMagnitude * kinematicsParams.airControl_maxSpeed;
                float2 currentToTarget = targetVelocity - velocity_last;
                // Acceleration needed to get us to the target velocity
                float2 accelToTarget = currentToTarget * (1f / deltaTime);
                float2 accel = math.normalizesafe(accelToTarget) * math.min(math.length(accelToTarget), kinematicsParams.airControl_maxAccel);
                // Only contribute energy that contributes to the desired velocity
                accel = forwardDir * math.dot(accel, forwardDir);
                spatialEnergy.acceleration += accel;
            }
            #endregion // Intentional movement energy - air control

            #region // Push up to stay above ground
            if (onGround)
            {
                // Snap the player position up to the correct height such that the feet align with the near foothold
                float yPosError = (foothold_near.position.y + (pawn.hipsHeight * .95f)) - pawn.hips.position.y;
                if (yPosError > 0)
                {
                    pawn.rigidbody.position += new Vector2(0, yPosError);
                }
            }
            #endregion


            // Negate all vertical momentum at start of a jump or bounce
            if (jumpStarted)
            {
                spatialEnergy.acceleration_impulse += -gravityDir * math.dot(velocity_last, gravityDir);
            }
            // Negate downward momentum if we're on the ground
            else
            {

                float downwardVelocityToCancel = max(0, math.dot((velocity_last - desiredDownwardVelocity), gravityDir));
                if (onGround && downwardVelocityToCancel > 0)
                {
                    spatialEnergy.acceleration_impulse += -gravityDir * downwardVelocityToCancel;
                }
            }

            #region // Air drag
            {
                float velocitySqrMag = math.lengthsq(pawn.rigidbody.velocity);
                if (velocitySqrMag > 0)
                {
                    spatialEnergy.force += kinematicsParams.drag_air * .5f * (-math.normalize(pawn.rigidbody.velocity) * velocitySqrMag);
                }
            }
            #endregion // Air drag

            #region // Integrate forces to determine new velocity
            // Integrate energy contributions to final velocity
            float2 pos_last = pawn.rigidbody.position;
            float2 velocity_spatial = velocity_last + IntegrateTransformEnergy(spatialEnergy, pawn.rigidbody.mass, deltaTime);
            // Write new state to physics world representation
            pawn.rigidbody.velocity = velocity_spatial;
            #endregion

            #region // Animation + Audio
            pawn.animator.SetBool(pawn.animParams.animParam_grounded.nameHash, onGround);
            if (jumpStarted)
            {
                pawn.animator.SetTrigger(pawn.animParams.animParam_jumpStart.nameHash);
                pawn.as_jump.Play();
            }
            float relativeSpeed = velocity_spatial.x - groundVelocity.x;
            pawn.animator.SetFloat(pawn.animParams.animParam_moveSpeed.nameHash, abs(relativeSpeed / kinematicsParams.maxRunSpeed));
            if (onGround && pawn.footstepEvent.ConsumeFlag())
            {
                if (moveInputMagnitude > 0)
                {
                    switch (foothold_primary.groundMaterial)
                    {
                        case GroundMaterial.WOOD:
                            pawn.as_footstep_platform.Play();
                            break;
                        case GroundMaterial.DEFAULT:
                        default:
                            pawn.as_footstep.Play();
                            break;
                    }
                }
            }
            if (moveInputMagnitude > .1f)
                pawn.spriteRenderer.flipX = (pawnInput.moveDir < 0) != pawn.spriteFacingLeft;

            if (!pawn.onGround_prev && onGround)
            {
                switch (foothold_primary.groundMaterial)
                {
                    case GroundMaterial.WOOD:
                        pawn.as_land_platform.Play();
                        break;
                    case GroundMaterial.DEFAULT:
                    default:
                        pawn.as_land.Play();
                        break;
                }
            }
            pawn.onGround_prev = onGround;
            #endregion
        }

        public static void
        RaycastRangeForFootholds(
              ref Foothold foothold_near
            , ref Foothold foothold_forward
            , Pawn pawn
            , PawnKinematicsParams kinematicsParams
            , float2 hipsPosition
            , float2 forwardDir
            , float2 gravityDir
            , float rangeBehind
            , float rangeAhead
            , bool skipFirstRay
            , int layerMask
            , RaycastHit2D[] cache_raycastHits
            )
        {
            float rangeWidth = (rangeAhead - -rangeBehind);
            int nRays = (int)ceil(rangeWidth * kinematicsParams.footholdRaycastResolution);
            float spacing = rangeWidth / nRays;
            for (int r = skipFirstRay ? 1 : 0; r < nRays + 1; r++)
            {
                float forwardDistance = -rangeBehind + spacing * r;
                float2 rayStart = hipsPosition + (forwardDir * forwardDistance);
                GizmosSetColor(ref GlobalDebug.gizmos_footholds, Colors.blue);
                GizmosDrawRay(ref GlobalDebug.gizmos_footholds, (Vector2)rayStart, (Vector2)gravityDir * pawn.hipsHeight * 2);
                int nHits = Physics2D.RaycastNonAlloc(rayStart, gravityDir, cache_raycastHits, pawn.hipsHeight * 2, layerMask);
                for (int h = 0; h < nHits; h++)
                {
                    RaycastHit2D hit = cache_raycastHits[h];
                    if (!hit.collider.isTrigger && hit.collider != pawn.physicsCollider && hit.fraction > 0)
                    {
                        float2 normal = hit.normal;
                        normal = math.normalize(normal);
                        float dot = math.dot(normal, -gravityDir);
                        if (dot > 1f - (kinematicsParams.maxSlope / 180))
                        {
                            GizmosSetColor(ref GlobalDebug.gizmos_footholds, Colors.coral);
                            GizmosDrawRay(ref GlobalDebug.gizmos_footholds, (Vector2)rayStart, (Vector2)gravityDir * pawn.hipsHeight * hit.fraction * 2);

                            if (!foothold_near.valid || abs(forwardDistance) < abs(foothold_near.forwardDistance))
                            {
                                foothold_near = new Foothold
                                {
                                    valid = true,
                                    forwardDistance = forwardDistance,
                                    forwardDistanceNormalized = forwardDistance / rangeWidth,
                                    position = hit.point,
                                    normal = normal,
                                    colliderID = hit.collider.GetInstanceID(),
                                    /// HACK(stef): 
                                    groundMaterial = hit.collider.sharedMaterial == null ? GroundMaterial.DEFAULT : GroundMaterial.WOOD,
                                    rayHitDistance = hit.distance,
                                };
                            }

                            if (forwardDistance > .01f && (!foothold_forward.valid || forwardDistance > foothold_forward.forwardDistance))
                            {
                                foothold_forward = new Foothold
                                {
                                    valid = true,
                                    forwardDistance = forwardDistance,
                                    forwardDistanceNormalized = forwardDistance / rangeWidth,
                                    position = hit.point,
                                    normal = normal,
                                    colliderID = hit.collider.GetInstanceID(),
                                    /// HACK(stef): 
                                    groundMaterial = hit.collider.sharedMaterial == null ? GroundMaterial.DEFAULT : GroundMaterial.WOOD,
                                    rayHitDistance = hit.distance,
                                };
                            }
                        }
                    }
                }
            }
        }

        static float2
        IntegrateTransformEnergy(
              TransformEnergy energy
            , float mass
            , float deltaTime
            )
        {
            // Return final velocity contribution
            return (((energy.force / mass) + energy.acceleration) * deltaTime) + energy.acceleration_impulse;
        }
        #endregion

    }
}