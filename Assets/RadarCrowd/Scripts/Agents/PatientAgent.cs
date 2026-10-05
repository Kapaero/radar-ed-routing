using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    public enum PatientPhase
    {
        ToRegistration,
        WaitingRoom,
        AtAmbulanceBay,
        ToCorridorSpot,
        CorridorWaiting,
        ToWing,
        ToCubicle,
        InCubicle,
        AwaitPorter,
        ToImaging,
        AtImaging,
        FromImaging,
        Boarding,
        Departing,
        Done
    }

    public enum TransportKind
    {
        ToImaging,
        FromImaging,
        ToWard
    }

    /// <summary>
    /// Patient over the whole visit. Walk-in: entrance -> waiting room -> (cubicle free) wing entry -> route -> cubicle.
    /// Ambulance: stretcher at the ambulance entrance -> cubicle, or a trolley spot in a corridor while no cubicle is free.
    /// In the cubicle for the NHAMCS length of visit: physician and nurse visits, an optional imaging trip with a porter
    /// (radiology and back), then discharge on foot or, if admitted, boarding and transfer on a bed by a porter.
    /// Routes between the corridor network and the cubicle are chosen by the router; a blocked corridor is noticed on site.
    /// </summary>
    public class PatientAgent : CrowdAgent
    {
        const float WaypointReach = 1.2f;
        const float TargetReach = 0.9f;
        const float SightDistance = 6f;
        const float StuckSeconds = 5f;      // a travelling stretcher that has not advanced 0.5 m for this long is held up
        const float ParkedSearchRadius = 3.5f;

        public override AgentKind Kind => AgentKind.Patient;

        public PatientSpec Spec { get; private set; }
        public bool Critical => Spec.Critical;
        public int[] CubicleOrder => Spec.CubicleOrder;
        public bool Stretcher { get; private set; }
        public PatientPhase Phase { get; private set; }
        public Cubicle Cubicle { get; private set; }
        public Route Route { get; private set; }
        public CorridorSpot Spot { get; private set; }
        public float QueuedSince { get; private set; } = -1f;
        public bool BoardingInCorridor { get; private set; }
        public float StayEnd { get; private set; } = -1f;

        public bool IsMoving => Phase == PatientPhase.ToRegistration || Phase == PatientPhase.ToCorridorSpot || Phase == PatientPhase.ToWing
            || Phase == PatientPhase.ToCubicle || Phase == PatientPhase.ToImaging || Phase == PatientPhase.FromImaging || Phase == PatientPhase.Departing;
        public bool AtCubicle => !BoardingInCorridor && (Phase == PatientPhase.InCubicle || Phase == PatientPhase.Boarding
            || (Phase == PatientPhase.AwaitPorter && pendingTransport != TransportKind.FromImaging));
        public bool OnCorridorTrolley => trolley != null;
        public bool AwayForImaging => Phase == PatientPhase.ToImaging || Phase == PatientPhase.AtImaging || Phase == PatientPhase.FromImaging
            || (Phase == PatientPhase.AwaitPorter && pendingTransport == TransportKind.FromImaging);

        // timestamps (s); -1 = did not happen, -2 = before the run (warm start)
        public float TArrival = -1, TTriageStart = -1, TTriageEnd = -1, TQueued = -1, TDispatched = -1, TCubicle = -1, TFirstPhysician = -1, TImagingOut = -1, TImagingBack = -1;
        public float TStayEnd = -1, TDeparture = -1, TDone = -1;
        public float CorridorSeconds;
        public float CorridorSecondsAt(float now) => CorridorSeconds + (trolley != null ? now - parkedSince : 0f);
        public float WalkToCubicle = -1, WalkImaging, WalkOut = -1;
        public int Reroutes;
        public int BlockedEncounters;
        public int MakeWayCount;            // times staff had to move a parked trolley aside for this stretcher
        public bool DetourKnownBlockage;    // set by the router: all candidate routes are known to be blocked
        public int PlannedDetours;
        public string RouteIn = "";
        public string RouteOut = "";

        SimContext ctx;
        Vector3[] waypoints;
        int waypoint;
        bool freeNavigation;
        Vector3 finalTarget;
        Vector3 currentTarget;
        Vector3 requestedTarget;            // last destination given to the NavMesh agent
        Vector3 progressMark;
        float progressTime;
        float makeWayUntil;                 // > 0: waiting while staff move a parked trolley aside
        float replanAt;                     // > 0: re-issue the destination once the NavMesh has been re-carved
        float asideUntil;                   // > 0: this patient's corridor trolley has been moved aside until then
        int tripMakeWayMark;
        bool hasLeavePoint;
        Vector3 leavePoint;
        bool routeEndIsTarget;
        float imagingAt = -1f;
        float imagingEnd;
        float boardEnd;
        float parkedSince;
        float walkMark;
        string tripKind;          // trip under way (null if none), logged on arrival
        float tripStart;
        float tripWalkMark;
        int tripBlockedMark;
        int tripRerouteMark;
        TransportKind pendingTransport;
        StaffAgent porter;
        GameObject trolley;
        NavMeshPath probe;

        protected override void Awake()
        {
            base.Awake();
            probe = new NavMeshPath();
        }

        // ------------------------------------------------------------------ spawning

        public void InitArrival(PatientSpec spec, SimContext context, float now)
        {
            Spec = spec;
            ctx = context;
            TArrival = now;
            if (spec.Ambulance && ctx.Layout.HasStretcherNavMesh)
            {
                SetMode(true);
                Phase = PatientPhase.AtAmbulanceBay;
                Queue(now);
                return;
            }
            SetMode(false);
            Phase = PatientPhase.ToRegistration;
            Go(ctx.Layout.RegistrationPos);
        }

        /// <summary>Warm start: the patient is already in the cubicle; the initial assessment is assumed done.</summary>
        public void InitInCubicle(PatientSpec spec, Cubicle cubicle, SimContext context, float now)
        {
            Spec = spec;
            ctx = context;
            SetMode(false);
            Cubicle = cubicle;
            cubicle.Occupant = this;
            TArrival = TQueued = TDispatched = TCubicle = TFirstPhysician = -2f;
            Phase = PatientPhase.InCubicle;
            Stop();
            StayEnd = now + Mathf.Max(ctx.Cfg.minStayMin, spec.LovMin - spec.ElapsedMin) * 60f;
            float imagingMinute = spec.ImagingFraction * spec.LovMin;
            if (spec.Imaging && imagingMinute > spec.ElapsedMin)
                imagingAt = now + (imagingMinute - spec.ElapsedMin) * 60f;
        }

        /// <summary>Triage done at the registration desk: the patient now waits for a cubicle.</summary>
        public void OnTriaged(float start, float end)
        {
            TTriageStart = start;
            TTriageEnd = end;
            Queue(end);
        }

        /// <summary>Warm start with exit block: an admitted patient already waiting for a bed on a corridor trolley.</summary>
        public void InitBoardingInCorridor(PatientSpec spec, CorridorSpot spot, SimContext context, float now)
        {
            Spec = spec;
            ctx = context;
            SetMode(true);
            TArrival = TQueued = TDispatched = TCubicle = TFirstPhysician = TStayEnd = -2f;
            BoardingInCorridor = true;
            Spot = spot;
            spot.Occupant = this;
            boardEnd = now + Mathf.Max(1f, spec.BoardMin * ctx.Cfg.boardingScale - spec.ElapsedMin) * 60f;
            Park(now);
        }

        void Queue(float now)
        {
            TQueued = now;
            QueuedSince = now;
            Stop();
            ctx.Hospital.Enqueue(this);
        }

        // ------------------------------------------------------------------ called by the hospital and the staff

        public void GoToCorridorSpot(CorridorSpot spot, float now)
        {
            Spot = spot;
            Phase = PatientPhase.ToCorridorSpot;
            Resume();
            Go(spot.Position);
        }

        public void BeginTransfer(Cubicle cubicle, Route route, float now)
        {
            Cubicle = cubicle;
            Route = route;
            TDispatched = now;
            walkMark = WalkedDistance;
            BeginTrip("inbound", now);
            if (Phase == PatientPhase.CorridorWaiting || Phase == PatientPhase.ToCorridorSpot)
                LeaveSpot(now);
            Resume();
            if (Phase == PatientPhase.WaitingRoom)
            {
                Phase = PatientPhase.ToWing;
                Go(route.Waypoints[0]);
                return;
            }
            RouteIn = route.Signature;
            Travel(route, route.Waypoints[route.Waypoints.Length - 1], false, Vector3.zero);
            Phase = PatientPhase.ToCubicle;
        }

        public void OnStaffVisit(StaffRole role, float now)
        {
            if (role == StaffRole.Physician && TFirstPhysician == -1f)
                TFirstPhysician = now;
        }

        /// <summary>The porter has arrived: start the transport.</summary>
        public void StartTransport(TransportKind kind, StaffAgent assignedPorter, float now)
        {
            porter = assignedPorter;
            Resume();
            switch (kind)
            {
                case TransportKind.ToImaging:
                    SetMode(Spec.Ambulance || Spec.Critical);
                    TImagingOut = now;
                    walkMark = WalkedDistance;
                    BeginTrip("imaging_out", now);
                    Route = ctx.Router.ChooseOutbound(this, Cubicle, now);
                    Travel(Route, ctx.Layout.RadiologyPos, true, ctx.Layout.RadiologyAccessPos);
                    Phase = PatientPhase.ToImaging;
                    break;
                case TransportKind.FromImaging:
                    BeginTrip("imaging_back", now);
                    Route = ctx.Router.ChooseInbound(this, Cubicle, now, "imaging");
                    Travel(Route, Route.Waypoints[Route.Waypoints.Length - 1], false, Vector3.zero);
                    Phase = PatientPhase.FromImaging;
                    break;
                case TransportKind.ToWard:
                    if (BoardingInCorridor)
                    {
                        // from the corridor trolley straight to the ward exit
                        LeaveSpot(now);
                        Resume();
                        TDeparture = now;
                        walkMark = WalkedDistance;
                        BeginTrip("ward_from_corridor", now);
                        GoFree(ctx.Layout.WardExitPos);
                        Phase = PatientPhase.Departing;
                        break;
                    }
                    SetMode(true);
                    Depart(now, ctx.Layout.WardExitPos);
                    break;
            }
        }

        // ------------------------------------------------------------------ simulation step

        public void Tick(float now)
        {
            TrackDistance();
            if (asideUntil > 0f && now >= asideUntil && !StretcherNear(2.5f))
            {
                asideUntil = 0f;
                if (trolley != null)
                    trolley.SetActive(true);   // pushed back to its place
            }
            if (IsMoving)
                WatchStuck(now);
            else
            {
                stuckSince = -1f;
                if (squeezing)
                {
                    squeezing = false;
                    Nav.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                }
            }
            if (Stretcher && IsMoving && HeldUp(now))
                return;
            switch (Phase)
            {
                case PatientPhase.ToRegistration:
                    if (Near(ctx.Layout.RegistrationPos, 2.5f))
                    {
                        Phase = PatientPhase.WaitingRoom;
                        Stop();
                        ctx.Hospital.EnqueueTriage(this);
                    }
                    break;

                case PatientPhase.ToCorridorSpot:
                    if (Near(Spot.Position, 1.6f))
                        Park(now);
                    break;

                case PatientPhase.ToWing:
                    if (Near(Route.Waypoints[0], 1.5f))
                    {
                        Route reconsidered = ctx.Router.ReconsiderInbound(this, Route, now);
                        if (reconsidered != Route)
                        {
                            Reroutes++;
                            Route = reconsidered;
                        }
                        RouteIn = Route.Signature;
                        Travel(Route, Route.Waypoints[Route.Waypoints.Length - 1], false, Vector3.zero);
                        Phase = PatientPhase.ToCubicle;
                    }
                    break;

                case PatientPhase.ToCubicle:
                    if (TravelTick(now))
                        ArriveCubicle(now);
                    break;

                case PatientPhase.InCubicle:
                    if (imagingAt >= 0f && now >= imagingAt)
                    {
                        imagingAt = -1f;
                        RequestPorter(TransportKind.ToImaging, now);
                    }
                    else if (now >= StayEnd)
                    {
                        TStayEnd = now;
                        if (Spec.Admitted)
                        {
                            Phase = PatientPhase.Boarding;
                            boardEnd = now + Spec.BoardMin * ctx.Cfg.boardingScale * 60f;
                            TryBoardInCorridor(now);
                        }
                        else
                        {
                            SetMode(false);
                            Depart(now, ctx.Layout.EntrancePos);
                        }
                    }
                    break;

                case PatientPhase.ToImaging:
                    if (TravelTick(now))
                    {
                        EndTrip(now);
                        Phase = PatientPhase.AtImaging;
                        Stop();
                        imagingEnd = now + ctx.Cfg.imagingMin * 60f;
                        ReleasePorter(now);
                    }
                    break;

                case PatientPhase.AtImaging:
                    if (now >= imagingEnd)
                        RequestPorter(TransportKind.FromImaging, now);
                    break;

                case PatientPhase.FromImaging:
                    if (TravelTick(now))
                    {
                        EndTrip(now);
                        TImagingBack = now;
                        WalkImaging = WalkedDistance - walkMark;
                        Phase = PatientPhase.InCubicle;
                        Stop();
                        ReleasePorter(now);
                    }
                    break;

                case PatientPhase.Boarding:
                    if (now >= boardEnd)
                        RequestPorter(TransportKind.ToWard, now);
                    else
                        TryBoardInCorridor(now);
                    break;

                case PatientPhase.CorridorWaiting:
                    if (BoardingInCorridor && now >= boardEnd)
                        RequestPorter(TransportKind.ToWard, now);
                    break;

                case PatientPhase.Departing:
                    if (TravelTick(now))
                        Finish(now);
                    break;
            }
        }

        void ArriveCubicle(float now)
        {
            EndTrip(now);
            TCubicle = now;
            WalkToCubicle = WalkedDistance - walkMark;
            Phase = PatientPhase.InCubicle;
            Stop();
            float elapsedMin = (now - TArrival) / 60f;
            StayEnd = now + Mathf.Max(ctx.Cfg.minStayMin, Spec.LovMin - elapsedMin) * 60f;
            if (Spec.Imaging)
                imagingAt = now + Spec.ImagingFraction * (StayEnd - now);
            ctx.Staff.RequestInitial(this, now);
        }

        /// <summary>UK practice: with someone waiting for a cubicle, a boarder is moved to a corridor trolley.</summary>
        void TryBoardInCorridor(float now)
        {
            if (!ctx.Cfg.boardInCorridor || BoardingInCorridor || ctx.Hospital.QueueLength == 0)
                return;
            CorridorSpot spot = ctx.Hospital.ClaimSpot(this);
            if (spot == null)
                return;
            BoardingInCorridor = true;
            ctx.Hospital.Release(Cubicle);
            SetMode(true);
            Spot = spot;
            Phase = PatientPhase.ToCorridorSpot;
            Resume();
            Go(spot.Position);
        }

        void BeginTrip(string kind, float now)
        {
            tripKind = kind;
            tripStart = now;
            tripWalkMark = WalkedDistance;
            tripBlockedMark = BlockedEncounters;
            tripRerouteMark = Reroutes;
            tripMakeWayMark = MakeWayCount;
        }

        void EndTrip(float now)
        {
            if (tripKind == null)
                return;
            string route = tripKind == "ward_from_corridor" || Route == null ? "" : Route.Signature;
            ctx.Metrics.LogTrip(this, tripKind, tripStart, now, route, WalkedDistance - tripWalkMark,
                BlockedEncounters - tripBlockedMark, Reroutes - tripRerouteMark, MakeWayCount - tripMakeWayMark);
            tripKind = null;
        }

        void RequestPorter(TransportKind kind, float now)
        {
            pendingTransport = kind;
            Phase = PatientPhase.AwaitPorter;
            Stop();
            ctx.Staff.RequestTransport(this, kind, now);
        }

        void ReleasePorter(float now)
        {
            if (porter != null)
                ctx.Staff.TransportDone(porter, now);
            porter = null;
        }

        void Depart(float now, Vector3 target)
        {
            TDeparture = now;
            walkMark = WalkedDistance;
            BeginTrip("outbound", now);
            ctx.Hospital.Release(Cubicle);
            Route = ctx.Router.ChooseOutbound(this, Cubicle, now);
            RouteOut = Route.Signature;
            Resume();
            Travel(Route, target, false, Vector3.zero);
            Phase = PatientPhase.Departing;
        }

        void Finish(float now)
        {
            EndTrip(now);
            TDone = now;
            WalkOut = WalkedDistance - walkMark;
            Phase = PatientPhase.Done;
            ReleasePorter(now);
            ctx.Metrics.RecordPatient(this);
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ corridor waiting on a trolley

        void Park(float now)
        {
            Phase = PatientPhase.CorridorWaiting;
            parkedSince = now;
            Nav.ResetPath();
            Nav.enabled = false;
            transform.SetPositionAndRotation(Spot.Position, Spot.Rotation);
            trolley = new GameObject("Corridor trolley");
            trolley.transform.SetParent(transform, false);
            trolley.AddComponent<NavMeshObstacle>();
            trolley.AddComponent<CorridorObstacle>().Configure(new Vector2(ctx.Cfg.trolleyWidth, ctx.Cfg.trolleyLength));
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(trolley.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            visual.transform.localScale = new Vector3(ctx.Cfg.trolleyWidth, 0.9f, ctx.Cfg.trolleyLength);
        }

        void LeaveSpot(float now)
        {
            asideUntil = 0f;
            if (trolley != null)   // parked: waiting for a cubicle, or for a ward bed and then for the porter
            {
                CorridorSeconds += now - parkedSince;
                if (trolley != null)
                    Destroy(trolley);
                trolley = null;
                transform.position = Spot.StandPosition;
                Nav.enabled = true;
                Nav.Warp(RouteBook.Snap(Spot.StandPosition, RouteBook.FilterFor(Nav.agentTypeID)));
            }
            ctx.Hospital.ReleaseSpot(Spot);
            Spot = null;
        }

        // ------------------------------------------------------------------ movement

        void SetMode(bool stretcher)
        {
            Stretcher = stretcher && ctx.Layout.HasStretcherNavMesh;
            ConfigureNavAgent(Spec.Speed, Stretcher ? ctx.Layout.StretcherAgentTypeId : 0, Stretcher ? ctx.Cfg.stretcherRadius : BodyRadius);
        }

        void Stop()
        {
            if (Nav.enabled && Nav.isOnNavMesh)
                Nav.isStopped = true;
        }

        void Resume()
        {
            if (Nav.enabled && Nav.isOnNavMesh)
                Nav.isStopped = false;
        }

        /// <summary>Follow a route; optionally leave it at a point and walk freely to a target beyond the corridor network.</summary>
        void Travel(Route route, Vector3 target, bool leaveEarly, Vector3 leaveAt)
        {
            waypoints = route.Waypoints;   // points every 4 m along the planned corridors: a closure is noticed only in sight
            finalTarget = target;
            hasLeavePoint = leaveEarly;
            leavePoint = leaveAt;
            routeEndIsTarget = FlatDistance(waypoints[waypoints.Length - 1], target) < 0.5f;
            freeNavigation = false;
            // continue from the nearest point of the route in direct view (not one behind a wall)
            NavMeshQueryFilter filter = RouteBook.FilterFor(Nav.agentTypeID);
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < waypoints.Length; i++)
            {
                float d = FlatDistance(transform.position, waypoints[i]);
                if (d >= best || NavMesh.Raycast(transform.position, waypoints[i], out _, filter))
                    continue;
                best = d;
                nearest = i;
            }
            waypoint = Mathf.Min(nearest + 1, waypoints.Length - 1);
            if (DetourKnownBlockage)
            {
                // informed, but every candidate route is reported blocked: the patient keeps the shortest route and finds
                // its way on site like an uninformed one (free navigation here would use the true state of the corridors)
                DetourKnownBlockage = false;
                PlannedDetours++;
                ctx.Metrics.LogEvent(SimClock.Now, "all_routes_blocked", Id, Route != null ? Route.Signature : "", transform.position);
            }
            SetWaypoint(SimClock.Now);
        }

        /// <summary>Advances the travel; true when the final target is reached.</summary>
        bool TravelTick(float now)
        {
            if (!freeNavigation)
            {
                if (hasLeavePoint && Near(leavePoint, 3f))
                {
                    GoFree(finalTarget);
                }
                else if (waypoint >= waypoints.Length - 1 && Near(waypoints[waypoints.Length - 1], TargetReach))
                {
                    if (routeEndIsTarget)
                        return true;
                    GoFree(finalTarget);
                }
                else if (waypoint < waypoints.Length - 1 && Near(currentTarget, WaypointReach))
                {
                    waypoint++;
                    SetWaypoint(now);
                }
            }
            return freeNavigation && Near(finalTarget, Stretcher ? 1.6f : TargetReach);
        }

        void Go(Vector3 target)
        {
            requestedTarget = target;
            progressMark = transform.position;
            progressTime = SimClock.Now;
            Nav.SetDestination(target);
        }

        // ------------------------------------------------------------------ trolleys parked on both sides

        /// <summary>
        /// A stretcher that cannot pass between corridor trolleys waits while staff push one of them aside
        /// (makeWaySec), then continues. True while the stretcher is held up (the phase logic is skipped).
        /// </summary>
        bool HeldUp(float now)
        {
            if (makeWayUntil > 0f)
            {
                if (now < makeWayUntil)
                    return true;
                makeWayUntil = 0f;
                PatientAgent parked = NearestParkedTrolley(ParkedSearchRadius);
                if (parked != null)
                {
                    parked.MoveAside(now, ctx.Cfg.makeWayPassSec);
                    ctx.Metrics.LogEvent(now, "make_way_done", Id, "trolley of patient " + parked.Id, transform.position);
                }
                replanAt = now + 2f * ctx.Cfg.dt;   // let the NavMesh drop the carved hole first
                return true;
            }
            if (replanAt > 0f)
            {
                if (now < replanAt)
                    return true;
                replanAt = 0f;
                Resume();
                Go(requestedTarget);
                return false;
            }
            if (FlatDistance(transform.position, progressMark) > 0.5f)
            {
                progressMark = transform.position;
                progressTime = now;
                return false;
            }
            if (now - progressTime < StuckSeconds || Nav.pathStatus == NavMeshPathStatus.PathComplete
                || NearestParkedTrolley(ParkedSearchRadius) == null)
                return false;   // still moving, only held up by people, or not next to parked trolleys
            MakeWayCount++;
            makeWayUntil = now + ctx.Cfg.makeWaySec;
            Stop();
            ctx.Metrics.LogEvent(now, "make_way", Id, Route != null ? Route.Signature : "", transform.position);
            return true;
        }

        // ------------------------------------------------------------------ diagnostics: travelling but not moving

        const float SqueezeAfter = 10f;   // s without progress before people step aside
        const float LeadAfter = 30f;      // s at the end of a partial path before staff lead the stretcher on
        bool squeezing;
        Vector3 squeezeMark;
        Vector3 stuckMark;
        float stuckSince = -1f;
        float nextStuckLog;

        void WatchStuck(float now)
        {
            if (squeezing && FlatDistance(transform.position, squeezeMark) > 2f)
            {
                squeezing = false;
                Nav.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            }
            if (stuckSince < 0f || FlatDistance(transform.position, stuckMark) > 0.5f)
            {
                stuckMark = transform.position;
                stuckSince = now;
                nextStuckLog = now + 60f;
                return;
            }
            float held = now - stuckSince;
            // people step aside for a patient who cannot get past them (the NavMesh path is open): squeeze through
            if (held >= SqueezeAfter && !squeezing && makeWayUntil <= 0f && Nav.enabled && Nav.isOnNavMesh
                && Nav.pathStatus == NavMeshPathStatus.PathComplete)
            {
                squeezing = true;
                squeezeMark = transform.position;
                Nav.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
                ctx.Metrics.LogEvent(now, "squeeze", Id, Stretcher ? "stretcher" : "walking", transform.position);
            }
            // the path ends short of the target and no trolley can be moved: staff lead the stretcher the last metres
            if (held >= LeadAfter && Nav.enabled && Nav.isOnNavMesh && Nav.pathStatus == NavMeshPathStatus.PathPartial
                && makeWayUntil <= 0f && NearestParkedTrolley(ParkedSearchRadius) == null)
            {
                Vector3 to = NavMesh.SamplePosition(requestedTarget, out NavMeshHit hit, 3f, RouteBook.FilterFor(Nav.agentTypeID)) ? hit.position : requestedTarget;
                ctx.Metrics.LogEvent(now, "led_to_target", Id, Phase.ToString(), transform.position);
                Nav.Warp(to);
                Go(requestedTarget);
                stuckSince = -1f;
                return;
            }
            if (now < nextStuckLog)
                return;
            nextStuckLog = now + 300f;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(Phase).Append(Stretcher ? " stretcher" : " walking").Append(" for ").Append((now - stuckSince).ToString("0", inv)).Append(" s")
              .Append(" path ").Append(Nav.enabled ? Nav.pathStatus.ToString() : "disabled")
              .Append(" stopped ").Append(Nav.enabled && Nav.isOnNavMesh && Nav.isStopped)
              .Append(" remaining ").Append(Nav.enabled && Nav.isOnNavMesh ? Nav.remainingDistance.ToString("0.0", inv) : "-")
              .Append(" target ").Append(requestedTarget.x.ToString("0.0", inv)).Append(' ').Append(requestedTarget.z.ToString("0.0", inv))
              .Append(" makeWay ").Append(makeWayUntil > 0f).Append(" replan ").Append(replanAt > 0f).Append(" | near:");
            foreach (CrowdAgent a in AgentRegistry.Agents)
            {
                if (a == this) continue;
                float d = FlatDistance(transform.position, a.Position);
                if (d > 2.5f) continue;
                string kind = a is PatientAgent pa ? (pa.OnCorridorTrolley ? "trolley" : pa.Stretcher ? "stretcher" : "patient") + "/" + pa.Phase : a.Kind.ToString();
                sb.Append(' ').Append(kind).Append(a.IsStanding ? "(standing)" : "(moving)").Append('@').Append(d.ToString("0.0", inv));
            }
            ctx.Metrics.LogEvent(now, "stuck", Id, sb.ToString(), transform.position);
        }

        PatientAgent NearestParkedTrolley(float radius)
        {
            PatientAgent best = null;
            float bestDistance = radius;
            foreach (CrowdAgent a in AgentRegistry.Agents)
            {
                if (a is PatientAgent p && p != this && p.trolley != null && p.trolley.activeSelf)
                {
                    float d = FlatDistance(transform.position, p.transform.position);
                    if (d < bestDistance) { best = p; bestDistance = d; }
                }
            }
            return best;
        }

        bool StretcherNear(float radius)
        {
            foreach (CrowdAgent a in AgentRegistry.Agents)
                if (a is PatientAgent p && p != this && p.Stretcher && p.IsMoving && FlatDistance(transform.position, p.transform.position) < radius)
                    return true;
            return false;
        }

        /// <summary>Staff push this patient's corridor trolley aside (into a doorway) for a passing stretcher.</summary>
        public void MoveAside(float now, float seconds)
        {
            if (trolley == null)
                return;
            trolley.SetActive(false);
            asideUntil = now + seconds;
        }

        void GoFree(Vector3 target)
        {
            freeNavigation = true;
            Go(target);
        }

        void SetWaypoint(float now)
        {
            // waypoints were laid out on the empty corridor; points an obstacle now stands on are skipped, so the patient
            // aims at the next free point beyond it (around a trolley, or into the closure it is about to notice)
            NavMeshQueryFilter filter = RouteBook.FilterFor(Nav.agentTypeID);
            while (waypoint < waypoints.Length - 1 && !NavMesh.SamplePosition(waypoints[waypoint], out _, 0.3f, filter))
                waypoint++;
            // interpolated points can lie a few centimetres off the mesh next to a wall: aim at the point on the mesh
            Vector3 target = waypoints[waypoint];
            if (NavMesh.SamplePosition(target, out NavMeshHit onMesh, 0.3f, filter) || NavMesh.SamplePosition(target, out onMesh, 2.5f, filter))
                target = onMesh.position;
            currentTarget = target;
            float straight = FlatDistance(transform.position, target);
            bool reachable = Nav.CalculatePath(target, probe) && (probe.status == NavMeshPathStatus.PathComplete
                || (probe.status == NavMeshPathStatus.PathPartial && probe.corners.Length > 0
                    && FlatDistance(probe.corners[probe.corners.Length - 1], target) <= 0.3f));
            float pathLength = reachable ? PathLength(probe) : float.PositiveInfinity;
            bool detour = straight <= SightDistance && pathLength > 2f * straight + 3f;
            if (detour && !ObstructionNear(transform.position, target, ctx.Router.RequiredWidth(this)))
            {
                // nothing stands in the way: the point was cut off next to a wall by the NavMesh, aim at the next one
                if (waypoint < waypoints.Length - 1)
                {
                    waypoint++;
                    SetWaypoint(now);
                    return;
                }
            }
            else if (detour)
            {
                // the corridor ahead is blocked: noticed on site, the patient now navigates around it
                BlockedEncounters++;
                ctx.Metrics.LogEvent(now, "blocked", Id, (Route != null ? Route.Signature : "") + " wp " + waypoint + "/" + waypoints.Length
                    + " straight " + straight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " path " + (reachable ? pathLength.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "none")
                    + " target " + target.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + target.z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + (Stretcher ? " stretcher" : ""), transform.position);
                GoFree(finalTarget);
                return;
            }
            Go(target);
        }

        /// <summary>
        /// An obstacle next to the stretch from a to b that really leaves less than the required width in its corridor
        /// cell (a closure, or trolleys facing each other for a stretcher); a single trolley in a wide corridor is not one.
        /// </summary>
        bool ObstructionNear(Vector3 a, Vector3 b, float requiredWidth)
        {
            IReadOnlyList<CorridorObstacle> obstacles = AgentRegistry.Obstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                CorridorObstacle o = obstacles[i];
                if (!o.isActiveAndEnabled || DistanceToSegment(o.transform.position, a, b) > 1.5f + 0.5f * o.Footprint.magnitude)
                    continue;
                CorridorCell cell = null;
                foreach (CorridorCell c in ctx.Layout.Cells)
                    if (c.Contains(o.transform.position)) { cell = c; break; }
                if (cell == null || cell.TrueFreeWidth() < requiredWidth)
                    return true;
            }
            return false;
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector2 ap = new Vector2(p.x - a.x, p.z - a.z), ab = new Vector2(b.x - a.x, b.z - a.z);
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
            return (ap - ab * t).magnitude;
        }

        bool Near(Vector3 target, float radius)
        {
            return FlatDistance(transform.position, target) <= radius;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float PathLength(NavMeshPath path)
        {
            float length = 0f;
            Vector3[] c = path.corners;
            for (int i = 0; i + 1 < c.Length; i++)
                length += Vector3.Distance(c[i], c[i + 1]);
            return length;
        }
    }
}
