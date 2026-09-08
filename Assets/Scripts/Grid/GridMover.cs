using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Grid
{
    /// The A*-planned "walk a route of waypoints" helper every creature agent
    /// needs — composed into an agent like Creature / Hunger / Combatant
    /// rather than pasted into each one. The five Monster agents and the Imp
    /// all drove byte-identical copies of PlanPathTo / MoveAlongPathThen /
    /// the five backing fields before this existed; they now hold one of
    /// these and forward to it.
    ///
    /// Combat keeps its own path/move copy for now (its waypoints are all
    /// cell centres and it steps without an on-arrive callback — see
    /// Combatant); folding that in is the remaining half of the netcode
    /// prerequisite the design doc tracks.
    public sealed class GridMover
    {
        private DungeonGrid _grid;
        private Transform _tf;
        private Func<float> _moveSpeed;
        private bool _isImp;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        // The goal last handed to PlanPathTo — kept so Replan can re-run the
        // exact same call after the agent's position changes out from under
        // it (see MinionGrabController / each agent's own
        // ReplanPathFromCurrentPosition).
        public Vector2Int LastGoalCoord { get; private set; }
        public Vector3 LastGoalWorldPos { get; private set; }

        public void Initialize(DungeonGrid grid, Transform tf, Func<float> moveSpeed, bool isImp = false)
        {
            _grid = grid;
            _tf = tf;
            _moveSpeed = moveSpeed;
            _isImp = isImp;
        }

        /// Builds the world-space waypoint list to walk: every A*-planned grid
        /// cell up to (but not including) the last, then finalWorldPos exactly
        /// — which may differ slightly from that last cell's centre (e.g. an
        /// Imp's slot-1 lateral offset, or a Lair's true centre point).
        /// Returns false, with the waypoint list left empty, if no path
        /// exists — the caller is expected to not transition into a moving
        /// state in that case rather than falling back to a straight line
        /// through whatever's blocking the way.
        public bool PlanPathTo(Vector2Int goalCoord, Vector3 finalWorldPos)
        {
            LastGoalCoord = goalCoord;
            LastGoalWorldPos = finalWorldPos;

            var startCoord = _grid.WorldToGrid(_tf.position);
            var found = AStarPathfinder.TryFindPath(_grid, startCoord, goalCoord, _gridPathBuffer, _isImp);

            _waypoints.Clear();
            _waypointIndex = 0;

            if (!found)
            {
                return false;
            }

            for (int i = 0; i < _gridPathBuffer.Count - 1; i++)
            {
                _waypoints.Add(_grid.GridToWorld(_gridPathBuffer[i]));
            }

            _waypoints.Add(finalWorldPos);
            return true;
        }

        /// Advances along the current waypoint list this frame; invokes
        /// onArrive the frame the last waypoint is reached (and immediately if
        /// there's nothing to walk).
        public void MoveAlongPathThen(Action onArrive)
        {
            if (_waypointIndex >= _waypoints.Count)
            {
                onArrive();
                return;
            }

            var target = _waypoints[_waypointIndex];
            var flatTarget = new Vector3(target.x, _tf.position.y, target.z);
            _tf.position = Vector3.MoveTowards(_tf.position, flatTarget, _moveSpeed() * Time.deltaTime);
            if (Vector3.Distance(_tf.position, flatTarget) < 0.05f)
            {
                _waypointIndex++;
                if (_waypointIndex >= _waypoints.Count)
                {
                    onArrive();
                }
            }
        }

        /// Re-plans to the last goal from wherever the agent is right now.
        public bool Replan()
        {
            return PlanPathTo(LastGoalCoord, LastGoalWorldPos);
        }
    }
}
