﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using System.Linq;
using DarienEngine;

public delegate void DieCallback(GameObject caller);

[RequireComponent(typeof(UnitAudioManager))]
/// <summary>
/// Class <c>RTSUnit</c> represents core functionality for all units in the game. Implements common behavior 
/// between the playable units (BaseUnit) and the NP-units (BaseUnitAI), e.g. movement/pathfinding, attack routine, etc.
/// </summary>
public class RTSUnit : MonoBehaviour
{
    /// <summary>
    /// Class <c>RTSUnit.States</c> models the various states a unit may be in. Wraps static members for object reference
    /// and extension for string reference as States(...).Value.
    /// </summary>
    public class States
    {
        private States(string value) { Value = value; }

        public string Value { get; set; }

        public static States Moving { get { return new States("Moving"); } }
        public static States Parking { get { return new States("Parking"); } }
        public static States Standby { get { return new States("Standby"); } }
        public static States Ready { get { return new States("Ready"); } }
        public static States Conjuring { get { return new States("Conjuring"); } }
        public static States Attacking { get { return new States("Attacking"); } }
        public static States Guarding { get { return new States("Guarding"); } }
        public static States Patrolling { get { return new States("Patrolling"); } }
    }
    public States state { get; set; } = States.Standby;

    // Every RTSUnit will need a unique identifier
    public System.Guid uuid { get; set; } = System.Guid.NewGuid();

    // Basic initializing info
    public Factions faction;
    public PlayerNumbers playerNumber = PlayerNumbers.Player1;
    public UnitCategories unitType;
    public Sprite unitIcon;
    public string unitName;

    public enum BodyTypes
    {
        Default, Flesh, Armor, Wood, Scale, Stone
    }

    [Tooltip("Type of material this unit should model for hit sounds.")]
    public BodyTypes bodyType;

    // Health 
    public float maxHealth;
    private float currentHealth;
    public float health { get; set; } = 100;
    public float healthRechargeRate = 3.0f; // delay in seconds
    protected float nextHealthRecharge = 0;
    private float lastHitRechargeDelay = 5.0f;

    // Mana
    public int baseMana;
    public int mana { get; set; } = 0;
    protected float manaRechargeRate = 0.5f;
    protected float nextManaRecharge;
    public int manaIncome;
    public int manaStorage;
    public int buildCost;
    public float buildTime = 100;

    // Abilities
    public bool isKinematic = true; // @TODO: rename: canMove
    public float maxSpeed { get; set; }
    public bool isBuilder = false; // @TODO: rename: canReclaim (build & clean?)
    public bool canAttack = true;
    public bool canStop = true;
    public bool canGuard = false;
    public bool canPatrol = false;
    public bool canFly = false;
    // @TODO: more abilities
    // public bool canAnimate = false; // resurrection
    // public bool cantBeStoned = true; // turn-to-stone attacks do nothing

    public AttackBehavior _AttackBehavior { get; set; }
    public FlyingUnit _FlyingUnit { get; set; }
    public bool phaseDie = false; // If this unit just disappears on die

    // @TODO: check StrongholdScript; ideally BaseUnit should handle the facing routine so these vars can remain protected
    public bool facing { get; set; } = false;
    public bool isDead { get; set; } = false;
    public bool isParking { get; set; } = false;
    protected bool tryParkingDirection;
    protected CommandQueueItem nextCommandAfterParking;

    // CommandQueue designed to hold queue of any type of commands, e.g. move, build, attack, etc.
    public CommandQueue commandQueue = new CommandQueue();
    public CommandQueueItem currentCommand
    {
        get { return !commandQueue.IsEmpty() ? commandQueue.Peek() : null; }
        set
        {
            // Setting current command means resetting queue
            commandQueue.Clear();
            commandQueue.Enqueue(value);
        }
    }

    public Vector3 offset { get; set; } = new Vector3(1, 1, 1);
    protected RTSUnit super;

    // Re-use for HandleFacing
    private Vector3 faceDirection;
    private Quaternion targetRotation;

    // Misc
    protected int expPoints = 10;
    private float avoidanceRadius;
    private float doubleAvoidanceRadius;
    private static float avoidanceTickerTo = 0.0f;
    private static float avoidanceTickerFrom = 0.0f;

    public ParticleSystem bloodSystem;

    // Components
    protected Player mainPlayer;
    public NavMeshAgent _Agent { get; set; }
    public NavMeshObstacle _Obstacle { get; set; }
    protected Animator _Animator;
    public HumanoidUnitAnimator _HumanoidUnitAnimator { get; set; }
    public UnitAudioManager AudioManager { get; set; }

    protected List<GameObject> whoCanSeeMe = new List<GameObject>();

    protected float dieTime = 30;
    protected int patrolIndex = 0;

    private DieCallback dieCallback;

    public int clusterNum { get; set; } = -1;
    public bool isStriking = false;

    // Init called on Start
    protected void Init()
    {
        // Mock "super" variable so BaseUnit can access this parent class
        super = this;

        // Current health starts as maxHealth
        currentHealth = maxHealth;

        // Initialize NavMesh objects
        if (isKinematic)
        {
            _Agent = GetComponent<NavMeshAgent>();
            _Obstacle = GetComponent<NavMeshObstacle>();
            _Obstacle.enabled = false;
            avoidanceRadius = _Agent.radius;
            doubleAvoidanceRadius = _Agent.radius * 2;
            maxSpeed = _Agent.speed;
        }
        else
        {
            _Obstacle = GetComponent<NavMeshObstacle>();
        }

        // Get object offset values based on collider
        if (gameObject.GetComponent<BoxCollider>())
        {
            offset = gameObject.GetComponent<BoxCollider>().size;
        }
        else if (gameObject.GetComponent<CapsuleCollider>())
        {
            float r = gameObject.GetComponent<CapsuleCollider>().radius;
            offset = new Vector3(r, r, r);
        }

        // Unit selection behavior is attached to MainCamera
        mainPlayer = GameManager.Instance.PlayerMain.player;
        AudioManager = GetComponent<UnitAudioManager>();
        _Animator = GetComponent<Animator>();
        if (GetComponent<HumanoidUnitAnimator>())
            _HumanoidUnitAnimator = GetComponent<HumanoidUnitAnimator>();

        // Set up linkage with AttackBehavior component if canAttack
        if (canAttack)
        {
            _AttackBehavior = GetComponent<AttackBehavior>();
            if (_AttackBehavior == null)
                throw new System.Exception("Error: Unit can attack but no AttackBehavior (Script) was found.");
        }

        // Set up FlyingUnit linkage
        if (canFly)
        {
            _FlyingUnit = GetComponent<FlyingUnit>();
            if (_FlyingUnit == null)
                throw new System.Exception("Error: Unit can fly but no FlyingUnit (Script) was found.");
        }

        // Each unit on Start must group under appropriate player holder and add itself to virtual context
        Functions.AddUnitToPlayerContext(this);
    }

    protected float UpdateHealth()
    {
        // Slowly regenerate health automatically
        if (Time.time > nextHealthRecharge && health < 100)
        {
            // @TODO?: PlusHealth(healthIncrement);
            PlusHealth(1);
            nextHealthRecharge = Time.time + healthRechargeRate;
        }
        return health;
    }

    protected void HandleMovement()
    {
        if (isKinematic && _Agent.enabled && !commandQueue.IsEmpty())
        {
            bool inRange = MoveToPosition(currentCommand.commandPoint);
            if (inRange)
                commandQueue.Dequeue();

            // Parking should always be the first state of a new unit, even if it's just parking to transform.position
            if (isParking)
            {
                state = States.Parking;
                isParking = !inRange;
                // Here we can reliably say last state was Parking and now parking is done, next state set here
                if (!isParking && nextCommandAfterParking != null)
                    commandQueue.Enqueue(nextCommandAfterParking); // Enqueue new command for next state
            }
            else
                state = States.Moving;

            DebugNavPath();
            // InflateAvoidanceRadius();
        }
    }

    public bool MoveToPosition(Vector3 moveTo)
    {
        bool inRange = false;
        if (isKinematic && _Agent.enabled && moveTo != null)
        {
            _Agent.SetDestination(moveTo);

            if (!(inRange = IsInRangeOf(moveTo)))
            {
                // Add extra steering while moving, except for certain flying units
                if ((IsMoving() && !canFly) || (IsMoving() && canFly && _FlyingUnit.doQuickFacing))
                    HandleFacing(_Agent.steeringTarget, 0.25f);
            }
        }
        return inRange;
    }

    /* protected void InflateAvoidanceRadius()
    {
        if (!IsInRangeOf(currentCommand.commandPoint, 4))
        {
            // Increase the avoidance radius while on the move
            if (_Agent.radius != doubleAvoidanceRadius)
            {
                // Debug.Log("Increasing avoidance radius " + _Agent.radius);
                _Agent.radius = Mathf.Lerp(_Agent.radius, doubleAvoidanceRadius, avoidanceTickerTo);
                avoidanceTickerTo += 0.05f * Time.deltaTime;
            }
            avoidanceTickerFrom = 0;
        }
        else
        {
            // Set avoidance radius back to normal when close to the target
            if (_Agent.radius != avoidanceRadius)
            {
                // Debug.Log("Decreasing avoidance radius " + _Agent.radius);
                _Agent.radius = Mathf.Lerp(_Agent.radius, avoidanceRadius, avoidanceTickerFrom);
                avoidanceTickerFrom += 0.25f * Time.deltaTime;
            }
            avoidanceTickerTo = 0;
        }
    } */

    // Handle patrolling behavior. Roam just means patrol these points at random, not in a loop
    protected void Patrol(bool roam = false)
    {
        if (_Agent.enabled && !commandQueue.IsEmpty())
        {
            state = States.Patrolling;
            List<PatrolPoint> patrolPoints = currentCommand.patrolRoute.patrolPoints;
            if (patrolIndex >= patrolPoints.Count)
                patrolIndex = 0;
            bool inRange = MoveToPosition(patrolPoints[patrolIndex].point);

            if (inRange)
                patrolIndex = roam ? Random.Range(0, patrolPoints.Count) : patrolIndex + 1;
        }
    }

    public bool HandleFacing(Vector3 targetPosition, float threshold = 0.01f)
    {
        faceDirection = targetPosition - transform.position;
        faceDirection.y = 0; // This makes rotation only apply to y axis
        targetRotation = Quaternion.LookRotation(faceDirection);
        if (Quaternion.Angle(transform.rotation, targetRotation) <= threshold)
            return false;
        transform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, 10.0f * Time.deltaTime);
        return true;
    }

    protected void HandleBumping(RTSUnit bumpedUnit)
    {
        // If I bumped into someone who is not moving or doing anything else
        if (bumpedUnit.commandQueue.IsEmpty())
        {
            // Get my heading
            Vector3 forward = transform.forward;
            forward.y = 0;
            float headingAngle = Quaternion.LookRotation(forward).eulerAngles.y;
            
            // Determine my general direction of movement by angle
            int dir = 0;
            // Right
            if (headingAngle > 45 && headingAngle < 135)
                dir = 1;
            // Down
            else if (headingAngle > 135 && headingAngle < 225)
                dir = 2;
            // Left
            else if (headingAngle > 225 && headingAngle < 315)
                dir = -1;
            // Up
            else if (headingAngle > 315 && headingAngle < 360)
                dir = 0;

            // Determine whether the bumped unit should move clockwise or counter clockwise
            bool CClockwise = false;
            Vector3 a = transform.position;
            Vector3 b = bumpedUnit.transform.position;
            CClockwise = (dir == 1 && a.z < b.z) || (dir == -1 && a.z > b.z) || (dir == 0 && a.x > b.x) || (dir == 2 && a.x < b.x);

            // Move the bumped unit perpendicular to the way I am traveling 
            bumpedUnit.MoveOutOfTheWay(this, CClockwise);
        }
    }

    public void MoveOutOfTheWay(RTSUnit sendingUnit, bool turnCClockwise)
    {
        // 2) When bumping propagates through a group, some units get to stepping on each other's toes. More logic is needed to ensure
        //    where I am moving does not end up creating a jumbled mess with conflicting points. E.g. test if point is already occupied, 
        //    test if the area around me has many units, etc.

        Vector3 dirToMove;
        if (turnCClockwise)
            dirToMove = Functions.Rotate90CCW(transform.position - sendingUnit.transform.position).normalized;
        else
            dirToMove = Functions.Rotate90CW(transform.position - sendingUnit.transform.position).normalized;
        SetMove(transform.position + (dirToMove * _Agent.radius));
    }

    private void OnTriggerEnter(Collider col)
    {
        string compareTag = gameObject.tag == "Enemy" ? "Friendly" : "Enemy";
        // Handle bumping behavior when unit bumps inner trigger
        if (col.isTrigger && col.gameObject.layer == LayerMask.NameToLayer("Inner Trigger") && col.gameObject.GetComponentInParent<RTSUnit>())
            HandleBumping(col.gameObject.GetComponentInParent<RTSUnit>());
        // Add collided unit to enemiesInSight
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == LayerMask.NameToLayer("Unit") && !col.isTrigger && canAttack && col.gameObject.GetComponent<RTSUnit>())
        {
            // Set a callback function to go off when the enemy unit dies to remove it from enemiesInSight
            col.gameObject.GetComponent<RTSUnit>().OnDie((enemy) => { _AttackBehavior.enemiesInSight.Remove(enemy); });
            _AttackBehavior.enemiesInSight.Add(col.gameObject);
        }
        // Update whoCanSeeMe based on "Fog of War" mask layer trigger
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == LayerMask.NameToLayer("Fog of War"))
            whoCanSeeMe.Add(col.transform.parent.gameObject);
    }

    private void OnTriggerExit(Collider col)
    {
        string compareTag = gameObject.tag == "Enemy" ? "Friendly" : "Enemy";
        if (col.gameObject.tag == compareTag && col.gameObject.layer == LayerMask.NameToLayer("Unit") && !col.isTrigger && canAttack)
            _AttackBehavior.enemiesInSight.Remove(col.gameObject);
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == LayerMask.NameToLayer("Fog of War"))
            whoCanSeeMe.Remove(col.transform.parent.gameObject);
    }

    public void OnDie(DieCallback callback)
    {
        dieCallback = callback;
    }

    protected void DebugNavPath()
    {
        var path = _Agent.path;
        for (int i = 0; i < path.corners.Length - 1; i++)
        {
            Debug.DrawLine(path.corners[i], path.corners[i + 1], Color.red);
        }
    }

    public void SetMove(Vector3 position, bool addToQueue = false, bool attackMove = false)
    {
        // If not holding shift on this move, clear the moveto queue and make this position first in queue
        if (!addToQueue)
            commandQueue.Clear();
        commandQueue.Enqueue(new CommandQueueItem
        {
            commandType = CommandTypes.Move,
            commandPoint = position,
            isAttackMove = attackMove
        });
        isParking = false;
        if (canAttack)
            _AttackBehavior.ClearAttack();
        TryToggleToAgent();
    }

    public void SetParking(Vector3 position)
    {
        isParking = true;
        currentCommand = new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = position };
    }

    public void SetParking(Vector3 position, bool parkingDirectionToggle)
    {
        isParking = true;
        currentCommand = new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = position };
        tryParkingDirection = parkingDirectionToggle;
    }

    public void Begin(DarienEngine.Directions facingDir, Vector3 parkPosition, bool parkToggle, CommandQueueItem nextCmd)
    {
        // SetFacingDir(facingDir);
        if (isKinematic)
        {
            // @TODO: can we guarantee this won't be called before Awake? Where commandQueue.isAI is set
            SetParking(parkPosition, parkToggle);
        }
        nextCommandAfterParking = nextCmd;
    }

    public void SetPatrol(Vector3 patrolPoint, bool addToQueue = false)
    {
        // Patrol commands maintain all patrol points within one command
        CommandQueueItem newCommand = new CommandQueueItem
        {
            commandType = CommandTypes.Patrol,
            commandPoint = patrolPoint,
            patrolRoute = new PatrolRoute
            {
                // Initial patrol points if clicking single is just the transform.position when the click happened, and the click point
                patrolPoints = new List<PatrolPoint>()
                {
                    new PatrolPoint { point = transform.position },
                    new PatrolPoint { point = patrolPoint }
                }
            }
        };
        // If not holding shift, clear queue/set currentCommand as new patrol command
        if (!addToQueue)
            currentCommand = newCommand;
        else
        {
            CommandQueueItem lastCommand = commandQueue.Last;
            // If last command was a Patrol command, add the patrol point to that patrolRoute
            if (lastCommand != null && lastCommand.commandType == CommandTypes.Patrol)
            {
                lastCommand.patrolRoute.patrolPoints.Add(new PatrolPoint
                {
                    point = patrolPoint,
                    // @Note: only BaseUnit should instantiate a sticker
                    sticker = Instantiate(
                        GameManager.Instance.patrolCommandSticker,
                        new Vector3(patrolPoint.x, 1.1f, patrolPoint.z),
                        GameManager.Instance.patrolCommandSticker.transform.rotation
                    )
                });
            }
            // Otherwise, queue a new Patrol route
            else
                commandQueue.Enqueue(newCommand);
        }
    }

    public void PlusHealth(int amount)
    {
        health += amount;
    }

    public void ReceiveDamage(float amount)
    {
        // @TODO: unit should attack back if taking damage
        if (health > 0)
        {
            currentHealth -= amount;
            health = currentHealth * (100 / maxHealth);

            // Play the blood particle system for units with blood
            if (bloodSystem)
                bloodSystem.Play();
        }
        // Delay the health recharge after receiving damage
        nextHealthRecharge = Time.time + lastHitRechargeDelay;
    }

    protected void HandleDie()
    {
        isDead = true;
        if (canAttack)
            _AttackBehavior.ClearAttack();

        if (_Agent)
            _Agent.enabled = false;

        if (_Obstacle)
            _Obstacle.enabled = false;

        AudioManager.PlayDieSound();

        // @TODO: play animation, play the dust particle system, destroy this object, and finally instantiate corpse object
        // @TODO: some units don't have a die animation, e.g. Factory units like Barracks
        // also walls don't even have an animator component
        if (_Animator)
            _Animator.SetTrigger("die");

        // Call the die callback now if it was set
        if (dieCallback != null)
            dieCallback(gameObject);

        // Remove this unit from the player context
        Functions.RemoveUnitFromPlayerContext(this);

        // @TODO: need to figure out how to do the white ghost die thing
        // @TODO: buildings all need to die immediately and spawn a debris model and play an explosion type particle system
        if (phaseDie)
            dieTime = 0; // Remove immediately 
    }

    public void TryToggleToAgent()
    {
        if (isKinematic && !_Agent.enabled)
        {
            _Obstacle.enabled = false;
            _Agent.enabled = true;
        }
    }

    public void TryToggleToObstacle()
    {
        if (isKinematic && !_Obstacle.enabled)
        {
            _Agent.enabled = false;
            // @TODO: flying units should only become obstacles when they are touching ground
            if (!canFly)
                _Obstacle.enabled = true;
        }
    }

    public Vector3 GetVelocity()
    {
        return _Agent.velocity;
    }

    public void SetSpeed(float speed)
    {
        _Agent.speed = speed;
    }

    public void ResetMaxSpeed()
    {
        _Agent.speed = maxSpeed;
    }

    public bool IsMoving()
    {
        return isKinematic && _Agent.enabled && (_Agent.velocity - Vector3.zero).sqrMagnitude > 0f;
    }

    public bool IsAttacking()
    {
        return canAttack && _AttackBehavior.isAttacking;
    }

    // Important: called by AnimationEvents set on all melee animations in order to accurately determine when a melee strike should register
    public void SetStriking(int val)
    {
        isStriking = val == 1 ? true : false;
    }

    public bool IsInRangeOf(Vector3 pos)
    {
        // Shouldn't Pow fractions, results in smaller numbers
        float rangeToUse = _Agent.stoppingDistance;
        if (_Agent.stoppingDistance >= 1)
            rangeToUse = Mathf.Pow(_Agent.stoppingDistance, 2);
        return (transform.position - pos).sqrMagnitude < rangeToUse;
    }

    public bool IsInRangeOf(Vector3 pos, float rng)
    {
        float rangeToUse = rng;
        if (rng >= 1)
            rangeToUse = Mathf.Pow(rng, 2);
        return (transform.position - pos).sqrMagnitude < Mathf.Pow(rng, 2);
    }
}
