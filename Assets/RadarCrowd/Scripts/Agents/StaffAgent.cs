using UnityEngine;

namespace RadarCrowd
{
    public enum StaffRole
    {
        Nurse,
        Physician,
        Porter
    }

    /// <summary>
    /// Nurse, physician or porter. Works from the staff base: walks to a patient, stays at the bedside for the task
    /// duration (or, for a porter, escorts the patient to the destination), then returns to the base unless the
    /// dispatcher hands over the next task on the way.
    /// </summary>
    public class StaffAgent : CrowdAgent
    {
        enum State
        {
            Idle,
            ToTask,
            AtTask,
            Escorting,
            Returning
        }

        public override AgentKind Kind => AgentKind.Staff;

        public StaffRole Role { get; private set; }
        public int Number { get; private set; }
        public int Tasks { get; private set; }
        public bool Available => state == State.Idle || state == State.Returning;

        SimContext ctx;
        State state;
        StaffTask task;
        Vector3 taskPosition;
        float taskEnd;
        float nextRefresh;

        public void Init(StaffRole role, int number, SimContext context, float speed)
        {
            Role = role;
            Number = number;
            ctx = context;
            ConfigureNavAgent(speed);
            state = State.Idle;
        }

        public void Assign(StaffTask next, Vector3 position)
        {
            task = next;
            taskPosition = position;
            Tasks++;
            state = State.ToTask;
            Resume();
            Nav.stoppingDistance = 0.3f;
            Nav.SetDestination(position);
        }

        /// <summary>Called by the patient when an escorted transport has reached its destination.</summary>
        public void EndEscort()
        {
            task = null;
            ReturnToBase();
        }

        public void Tick(float now)
        {
            TrackDistance();
            switch (state)
            {
                case State.ToTask:
                    if (task.Patient == null && task.Kind != StaffTaskKind.Errand)
                    {
                        ctx.Staff.Abandon(this, task);
                        ReturnToBase();
                        break;
                    }
                    if (task.Kind == StaffTaskKind.Transport)
                        taskPosition = task.Patient.Position;   // the patient may be in the cubicle, in radiology or in a corridor
                    if (now >= nextRefresh)
                    {
                        nextRefresh = now + 1f;
                        Nav.SetDestination(taskPosition);
                    }
                    if (Near(taskPosition, task.Kind == StaffTaskKind.Transport ? 2f : task.Kind == StaffTaskKind.Errand ? 1.2f : 0.8f))
                        StartTask(now);
                    break;

                case State.AtTask:
                    if (now >= taskEnd)
                    {
                        StaffTask done = task;
                        task = null;
                        ReturnToBase();
                        ctx.Staff.BedsideDone(this, done, now);
                    }
                    break;

                case State.Escorting:
                    if (task == null || task.Patient == null)
                    {
                        task = null;
                        ReturnToBase();
                        break;
                    }
                    if (now >= nextRefresh)
                    {
                        nextRefresh = now + 0.5f;
                        Nav.stoppingDistance = 1.2f;
                        Nav.SetDestination(task.Patient.Position);
                    }
                    break;

                case State.Returning:
                    if (Near(ctx.Layout.StaffBasePos, 2f))
                    {
                        state = State.Idle;
                        Stop();
                    }
                    break;
            }
        }

        void StartTask(float now)
        {
            if (task.Kind == StaffTaskKind.Errand)
            {
                state = State.AtTask;
                Stop();
                taskEnd = now + task.DurationSec;
                return;
            }
            if (task.Kind == StaffTaskKind.Transport)
            {
                state = State.Escorting;
                task.Patient.StartTransport(task.Transport, this, now);
                return;
            }
            if (!task.Patient.AtCubicle)
            {
                // the patient was taken away (e.g. to radiology): the task goes back to the queue
                ctx.Staff.Requeue(this, task);
                task = null;
                ReturnToBase();
                return;
            }
            state = State.AtTask;
            Stop();
            taskEnd = now + task.DurationSec;
            task.Patient.OnStaffVisit(Role, now);
        }

        void ReturnToBase()
        {
            state = State.Returning;
            Resume();
            Nav.stoppingDistance = 0.3f;
            Nav.SetDestination(ctx.Layout.StaffBasePos);
        }

        void Stop()
        {
            if (Nav.isOnNavMesh) Nav.isStopped = true;
        }

        void Resume()
        {
            if (Nav.isOnNavMesh) Nav.isStopped = false;
        }

        bool Near(Vector3 target, float radius)
        {
            float dx = transform.position.x - target.x, dz = transform.position.z - target.z;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
