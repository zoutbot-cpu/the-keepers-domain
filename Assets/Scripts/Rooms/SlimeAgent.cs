using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// A single bred slime — "little blue balls that move around" per the
    /// design doc. Wanders randomly among its owning Hatchery room's own
    /// footprint tiles (that rectangle is always convex and obstacle-free,
    /// so a straight line between any two of its tiles never leaves it —
    /// no pathfinding needed to keep a slime "on hatchery tiles" while it
    /// walks). Checks its own current tile every frame and disappears the
    /// instant that tile no longer belongs to this room — the Hatchery
    /// being sold clears every one of its tiles' RoomId before RoomSold
    /// even fires (see DungeonGrid.RemoveRoomTiles), so removal falls out
    /// of this one rule automatically rather than needing its own special
    /// case here or in SlimeHatcheryManager.
    public class SlimeAgent : MonoBehaviour
    {
        private const float MoveSpeed = 1f;
        private const float MinIdleSeconds = 0.5f;
        private const float MaxIdleSeconds = 2.5f;
        private const float ArriveThreshold = 0.05f;
        private const float Radius = 0.15f;

        [SerializeField] private Color _color = new Color(0.25f, 0.55f, 0.95f);

        private DungeonGrid _grid;
        private SlimeHatcheryManager _owner;
        private string _roomId;
        private List<Vector2Int> _roomTiles;
        private System.Random _rng;

        private bool _isMoving;
        private Vector3 _targetWorldPos;
        private float _idleTimer;
        private float _idleDuration;

        public void Initialize(DungeonGrid grid, SlimeHatcheryManager owner, string roomId, List<Vector2Int> roomTiles, Vector2Int spawnCoord)
        {
            _grid = grid;
            _owner = owner;
            _roomId = roomId;
            _roomTiles = roomTiles;
            // Seeded from Unity's own RNG rather than GetInstanceID() — no
            // need for a stable/reproducible seed here (unlike
            // GameBootstrap's resource scatter), just per-slime variety.
            _rng = new System.Random(UnityEngine.Random.Range(int.MinValue, int.MaxValue));

            transform.position = GroundedWorldPos(spawnCoord);
            BuildVisual();
            StartIdle();
        }

        private void Update()
        {
            var currentCoord = _grid.WorldToGrid(transform.position);
            if (_grid.GetTile(currentCoord).RoomId != _roomId)
            {
                Destroy(gameObject);
                return;
            }

            if (_isMoving)
            {
                TickMove();
            }
            else
            {
                TickIdle();
            }
        }

        private void TickIdle()
        {
            _idleTimer += Time.deltaTime;
            if (_idleTimer < _idleDuration)
            {
                return;
            }

            var targetCoord = _roomTiles[_rng.Next(_roomTiles.Count)];
            _targetWorldPos = GroundedWorldPos(targetCoord);
            _isMoving = true;
        }

        private void TickMove()
        {
            var flatTarget = new Vector3(_targetWorldPos.x, transform.position.y, _targetWorldPos.z);
            transform.position = Vector3.MoveTowards(transform.position, flatTarget, MoveSpeed * Time.deltaTime);
            if (Vector3.Distance(transform.position, flatTarget) < ArriveThreshold)
            {
                _isMoving = false;
                StartIdle();
            }
        }

        private void StartIdle()
        {
            _idleTimer = 0f;
            _idleDuration = MinIdleSeconds + (float)_rng.NextDouble() * (MaxIdleSeconds - MinIdleSeconds);
        }

        private Vector3 GroundedWorldPos(Vector2Int coord)
        {
            var worldPos = _grid.GridToWorld(coord);
            return new Vector3(worldPos.x, _grid.FloorSurfaceY + Radius, worldPos.z);
        }

        private void BuildVisual()
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "SlimeBall";
            visual.transform.SetParent(transform, false);
            visual.transform.localScale = Vector3.one * (Radius * 2f);
            Prims.Tint(visual, _color);
            Destroy(visual.GetComponent<Collider>());
        }

        /// Keeps SlimeHatcheryManager's live-list in sync whenever a slime
        /// disappears on its own (see Update's tile check) rather than
        /// being explicitly collected (SlimeHatcheryManager.CollectSlime
        /// already removes from that list itself before destroying, so this
        /// is a safe no-op in that case — List.Remove on an absent item just
        /// returns false).
        private void OnDestroy()
        {
            _owner?.NotifySlimeDestroyed(_roomId, this);
        }
    }
}
