using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.Rooms;

namespace KeepersDomain.Input
{
    /// The "grab hand" tool — a blocky low-poly hand that hovers over
    /// wherever the cursor points while Grab build mode is active (see
    /// TileInteractionController.BuildMode), standing in for the cursor
    /// itself rather than sitting alongside it. Open-handed by default;
    /// tapping a minion (impling, gremlin, warlock, maze rattler, bean
    /// counter, or elf) closes
    /// the hand around it and suspends it above the whole domain — above
    /// even undug Rock, so it always reads as floating clear of the
    /// terrain rather than skimming it — following the cursor until the
    /// player taps a walkable (dug, unblocked) tile to drop it back down.
    /// Tapping an unwalkable tile while carrying does nothing, same
    /// "invalid tap is just ignored" rule every other placement tool in
    /// this project follows.
    public class MinionGrabController : MonoBehaviour
    {
        [SerializeField] private Color _handColor = new Color(0.85f, 0.72f, 0.55f);
        // 40% larger than the original placeholder size, per playtest
        // feedback — the hand read as too small against the grid.
        [SerializeField] private float _palmSize = 0.49f;
        [SerializeField] private float _fingerLength = 0.392f;
        [SerializeField] private float _fingerThickness = 0.14f;

        // How far above Rock's own top face (0.5, see DungeonGrid's tile
        // layout) the hand hovers — high enough that it visibly clears
        // undug Rock everywhere on the map, not just dug-out Floor.
        [SerializeField] private float _hoverClearance = 1.2f;

        // Curl angle applied to each finger socket when the hand closes
        // around a grabbed minion — see BuildHandVisual/SetOpen.
        private const float ClosedCurlAngle = -75f;
        private const float RockTopY = 0.5f;

        private Camera _camera;
        private DungeonGrid _grid;
        private KeeperContext _active;
        private TrainingRoomManager _trainingRoomManager;
        private JailManager _jailManager;

        private GameObject _handRoot;
        private readonly List<Transform> _fingerSockets = new List<Transform>();
        private bool _isOpen = true;

        private bool _isCarrying;
        private Behaviour _carriedAgent;
        private Transform _carriedTransform;
        private Vector2Int _carryOriginCoord;

        /// Whether a minion is currently suspended in the hand — read by
        /// BottomMenuBar to show the right instruction text for Grab mode.
        public bool IsCarrying => _isCarrying;

        public void Initialize(Camera camera, DungeonGrid grid, KeeperContext[] contexts, int activeIndex)
        {
            _camera = camera;
            _grid = grid;
            SetActiveContext(contexts[activeIndex]);
            BuildHandVisual();
            SetVisible(false);
        }

        /// Repoints the grab hand at ctx's Training Room / Jail — and drops
        /// any carried minion first, since it belongs to the keeper we're
        /// switching away from. Called by LocalPlayerController on a debug
        /// player switch.
        public void SetActiveContext(KeeperContext ctx)
        {
            CancelCarry();
            _active = ctx;
            _trainingRoomManager = ctx.TrainingRoom;
            _jailManager = ctx.Jail;
        }

        public void SetVisible(bool visible)
        {
            if (_handRoot != null && _handRoot.activeSelf != visible)
            {
                _handRoot.SetActive(visible);
            }
        }

        /// Moves the hand (and whatever it's carrying, if anything) to
        /// hover above screenPos's grid column — called every frame Grab
        /// mode is active, mirroring how TileInteractionController
        /// tracks the pointer for its own preview/gesture logic.
        public void UpdateHover(Vector2 screenPos)
        {
            if (_handRoot == null || !_handRoot.activeSelf || _camera == null || _grid == null)
            {
                return;
            }

            if (!TryGetGroundPoint(screenPos, out var groundPoint))
            {
                return;
            }

            var hoverPos = new Vector3(groundPoint.x, RockTopY + _hoverClearance, groundPoint.z);
            _handRoot.transform.position = hoverPos;

            if (_isCarrying && _carriedTransform != null)
            {
                _carriedTransform.position = hoverPos;
            }
        }

        /// The Grab tool's single tap action — grabs whatever minion (if
        /// any) is standing on coord when the hand is empty, or drops the
        /// carried minion onto coord when it isn't. Called by
        /// TileInteractionController.EndGesture once a Grab-mode tap
        /// resolves.
        public void HandleTap(Vector2Int coord)
        {
            if (_isCarrying)
            {
                TryDrop(coord);
            }
            else
            {
                TryGrab(coord);
            }
        }

        /// Returns whatever's being carried to the tile it was grabbed
        /// from — that tile was walkable at grab time and nothing in this
        /// prototype can turn walkable Floor back into Rock, so it's
        /// always a safe place to set back down. Called by
        /// TileInteractionController.SetBuildMode when the player leaves
        /// Grab mode mid-carry, so a minion never ends up stuck floating
        /// forever just because the player switched tools.
        public void CancelCarry()
        {
            if (!_isCarrying)
            {
                return;
            }

            DropAt(_carryOriginCoord);
        }

        private void TryGrab(Vector2Int coord)
        {
            if (!TryFindMinionAt(coord, out var agent, out var creatureTransform))
            {
                return;
            }

            _isCarrying = true;
            _carriedAgent = agent;
            _carriedTransform = creatureTransform;
            _carryOriginCoord = coord;

            // Pausing the agent's own Update (rather than anything more
            // invasive) is enough — none of the four creature types touch
            // OnEnable/OnDisable, so this simply freezes its state machine
            // exactly where it was, ready to resume the instant it's
            // dropped. A minion grabbed mid-walk resumes with a stale path
            // computed from the tile it used to stand on — DropAt fixes
            // that up by asking the agent to replan from its new position
            // (see ReplanCarriedAgentPath) rather than leaving it to walk
            // the old route.
            agent.enabled = false;
            SetOpen(false);
        }

        private void TryDrop(Vector2Int coord)
        {
            // An Imp can't be set down on unbridged Water/Lava any more
            // than it can walk onto one on its own (see DungeonGrid.
            // IsWalkable) — every other carried creature type ignores the
            // distinction.
            if (!_grid.IsWalkable(coord, isImp: _carriedAgent is ImplingAgent))
            {
                return;
            }

            // Dropping a jailable creature (Gremlin/Warlock/Maze Rattler/
            // Elf — not Impling, not Bean Counter) onto a Jail pit tile
            // captures it instead of setting it back down: the Keeper
            // personally hauling a misbehaving minion off to prison. See
            // JailManager.TryCapture/ConversionClassManager.
            // TryTormentRandomPrisoner for what happens to it next.
            // Opportunistic — a full/unreachable Jail just falls through to
            // a normal drop-in-place instead of blocking the gesture.
            if (_jailManager != null && _jailManager.IsPitTile(coord)
                && TryGetJailableInfo(_carriedAgent, out var kind, out var name, out var level)
                && _jailManager.TryCapture(coord, kind, name, level, isGoodAlignment: false))
            {
                if (_carriedTransform != null)
                {
                    Destroy(_carriedTransform.gameObject);
                }

                _isCarrying = false;
                _carriedAgent = null;
                _carriedTransform = null;
                SetOpen(true);
                return;
            }

            // "Throwing" a minion onto the Training Room is a command, not
            // just a place-to-stand: it tells whichever creature type
            // actually trains (see ApplyTrainingPriorityIfDropped) to
            // prioritize training over its other productive tasks — but
            // still below hunger/claiming a Lair, see each agent's own
            // SetTrainingPriority. Checked here (drop time), not at grab
            // time, since the destination tile is what expresses the
            // player's intent.
            if (_trainingRoomManager != null && _trainingRoomManager.IsTrainingRoomTile(coord))
            {
                ApplyTrainingPriorityIfDropped();
            }

            DropAt(coord);
        }

        /// Whether agent is a creature Conversion Class's torment mechanic
        /// can process — every recruited/converted minion except Impling
        /// (mana-conjured, not a moral subject) and Bean Counter (the
        /// lecturer, not a candidate for its own class). Same "no shared
        /// interface, switch each concrete type" shape TryFindMinionAt/
        /// ReplanCarriedAgentPath already use in this file.
        private static bool TryGetJailableInfo(Behaviour agent, out string kind, out string name, out int level)
        {
            switch (agent)
            {
                case GremlinAgent gremlin:
                    kind = GremlinAgent.CreatureKind;
                    name = gremlin.Name;
                    level = gremlin.Creature.Level;
                    return true;
                case WarlockAgent warlock:
                    kind = WarlockAgent.CreatureKind;
                    name = warlock.Name;
                    level = warlock.Creature.Level;
                    return true;
                case MazeRattlerAgent mazeRattler:
                    kind = MazeRattlerAgent.CreatureKind;
                    name = mazeRattler.Name;
                    level = mazeRattler.Creature.Level;
                    return true;
                case ElfAgent elf:
                    kind = ElfAgent.CreatureKind;
                    name = elf.Name;
                    level = elf.Creature.Level;
                    return true;
                default:
                    kind = null;
                    name = null;
                    level = 0;
                    return false;
            }
        }

        /// Only Warlock actually has a competing task to out-prioritize —
        /// it otherwise tries Research (Library) before Training, unlike
        /// Gremlin/Maze Rattler, which already try Training before their
        /// own fallback (Roam/Haunt) with nothing to reorder. Imps don't
        /// train at all (see TrainingRoomManager's own header comment —
        /// they get exp from mining instead), so a dropped Impling just
        /// lands normally, no special-casing needed. Given all that,
        /// there's nothing for a dropped Gremlin/Maze Rattler/Impling to
        /// do here — only Warlock gets a case.
        private void ApplyTrainingPriorityIfDropped()
        {
            if (_carriedAgent is WarlockAgent warlock)
            {
                warlock.SetTrainingPriority(true);
            }
        }

        private void DropAt(Vector2Int coord)
        {
            var worldPos = _grid.GridToWorld(coord);
            if (_carriedTransform != null)
            {
                _carriedTransform.position = worldPos;
            }

            if (_carriedAgent != null)
            {
                _carriedAgent.enabled = true;
                ReplanCarriedAgentPath();
            }

            _isCarrying = false;
            _carriedAgent = null;
            _carriedTransform = null;
            SetOpen(true);
        }

        /// Tells whichever creature type was just set back down to replan
        /// its route to wherever it was actually headed, from its new
        /// position — see each agent type's own
        /// ReplanPathFromCurrentPosition for why (resuming the stale path
        /// it had before being grabbed could clip it straight through a
        /// wall). No shared interface exists across the four creature
        /// types (same "duplicated, not shared" shape their own movement
        /// code already uses), so this just checks each concrete type in
        /// turn.
        private void ReplanCarriedAgentPath()
        {
            switch (_carriedAgent)
            {
                case ImplingAgent impling:
                    impling.ReplanPathFromCurrentPosition();
                    break;
                case GremlinAgent gremlin:
                    gremlin.ReplanPathFromCurrentPosition();
                    break;
                case WarlockAgent warlock:
                    warlock.ReplanPathFromCurrentPosition();
                    break;
                case MazeRattlerAgent mazeRattler:
                    mazeRattler.ReplanPathFromCurrentPosition();
                    break;
                case BeanCounterAgent beanCounter:
                    beanCounter.ReplanPathFromCurrentPosition();
                    break;
                case ElfAgent elf:
                    elf.ReplanPathFromCurrentPosition();
                    break;
            }
        }

        /// Checks every creature list in turn (implings first, same order
        /// TileInteractionController.Inspect uses) for whichever one's grid
        /// coord matches — the first match wins if more than one creature
        /// somehow shares a tile. Only the active keeper's own creatures are
        /// grabbable: you can't pick up and reposition a rival's minion.
        private bool TryFindMinionAt(Vector2Int coord, out Behaviour agent, out Transform creatureTransform)
        {
            foreach (var impling in ImplingAgent.All)
            {
                if (impling.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(impling.Position) == coord)
                {
                    agent = impling;
                    creatureTransform = impling.transform;
                    return true;
                }
            }

            foreach (var gremlin in GremlinAgent.All)
            {
                if (gremlin.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(gremlin.Position) == coord)
                {
                    agent = gremlin;
                    creatureTransform = gremlin.transform;
                    return true;
                }
            }

            foreach (var warlock in WarlockAgent.All)
            {
                if (warlock.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(warlock.Position) == coord)
                {
                    agent = warlock;
                    creatureTransform = warlock.transform;
                    return true;
                }
            }

            foreach (var mazeRattler in MazeRattlerAgent.All)
            {
                if (mazeRattler.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(mazeRattler.Position) == coord)
                {
                    agent = mazeRattler;
                    creatureTransform = mazeRattler.transform;
                    return true;
                }
            }

            foreach (var beanCounter in BeanCounterAgent.All)
            {
                if (beanCounter.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(beanCounter.Position) == coord)
                {
                    agent = beanCounter;
                    creatureTransform = beanCounter.transform;
                    return true;
                }
            }

            foreach (var elf in ElfAgent.All)
            {
                if (elf.Creature.OwnerId == _active.OwnerId && _grid.WorldToGrid(elf.Position) == coord)
                {
                    agent = elf;
                    creatureTransform = elf.transform;
                    return true;
                }
            }

            agent = null;
            creatureTransform = null;
            return false;
        }

        private bool TryGetGroundPoint(Vector2 screenPos, out Vector3 groundPoint)
        {
            var ray = _camera.ScreenPointToRay(screenPos);
            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (groundPlane.Raycast(ray, out var distance))
            {
                groundPoint = ray.GetPoint(distance);
                return true;
            }

            groundPoint = default;
            return false;
        }

        /// Curls the fingers in (closed, gripping a minion) or out (open,
        /// empty) — each finger is a cube offset forward from a socket
        /// transform positioned at its base, so rotating the socket curls
        /// the whole finger around that base point instead of spinning it
        /// in place around its own center.
        private void SetOpen(bool isOpen)
        {
            if (_isOpen == isOpen)
            {
                return;
            }

            _isOpen = isOpen;
            var curlAngle = _isOpen ? 0f : ClosedCurlAngle;
            foreach (var socket in _fingerSockets)
            {
                socket.localRotation = Quaternion.Euler(curlAngle, 0f, 0f);
            }
        }

        /// A plain blocky hand — one flat palm cube plus four finger
        /// cubes fanned out around its far edge — same low-poly-primitives
        /// style every other placeholder visual in this project uses
        /// (see DungeonGrid/JailManager). Built once at Initialize time and
        /// just repositioned/re-curled afterward.
        private void BuildHandVisual()
        {
            _handRoot = new GameObject("GrabHand");
            _handRoot.transform.SetParent(transform, false);

            var palm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            palm.name = "Palm";
            palm.transform.SetParent(_handRoot.transform, false);
            palm.transform.localScale = new Vector3(_palmSize, _palmSize * 0.45f, _palmSize * 0.9f);
            palm.GetComponent<Renderer>().material.color = _handColor;
            Destroy(palm.GetComponent<Collider>());

            var fingerOffsets = new[]
            {
                new Vector3(-_palmSize * 0.36f, 0f, _palmSize * 0.5f),
                new Vector3(-_palmSize * 0.12f, 0f, _palmSize * 0.58f),
                new Vector3(_palmSize * 0.12f, 0f, _palmSize * 0.58f),
                new Vector3(_palmSize * 0.36f, 0f, _palmSize * 0.5f),
            };

            foreach (var offset in fingerOffsets)
            {
                var socket = new GameObject("FingerSocket");
                socket.transform.SetParent(_handRoot.transform, false);
                socket.transform.localPosition = offset;

                var finger = GameObject.CreatePrimitive(PrimitiveType.Cube);
                finger.name = "Finger";
                finger.transform.SetParent(socket.transform, false);
                finger.transform.localPosition = new Vector3(0f, 0f, _fingerLength * 0.5f);
                finger.transform.localScale = new Vector3(_fingerThickness, _fingerThickness, _fingerLength);
                finger.GetComponent<Renderer>().material.color = _handColor;
                Destroy(finger.GetComponent<Collider>());

                _fingerSockets.Add(socket.transform);
            }
        }
    }
}
