using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Input;

namespace KeepersDomain.CameraControl
{
    /// Pinch-zoom + two-finger pan for touch, with a scroll/right-drag fallback
    /// so the same rig is testable from the editor with a mouse.
    [RequireComponent(typeof(Camera))]
    public class IsoCameraController : MonoBehaviour
    {
        [SerializeField] private float _panSpeed = 1f;
        [SerializeField] private float _zoomSpeed = 0.02f;
        [SerializeField] private float _mouseScrollZoomSpeed = 2f;
        [SerializeField] private float _minOrthoSize = 4f;
        [SerializeField] private float _maxOrthoSize = 20f;
        [SerializeField] private Vector2 _panBoundsMin = new Vector2(-5f, -5f);
        [SerializeField] private Vector2 _panBoundsMax = new Vector2(30f, 30f);
        [SerializeField] private float _rotateSpeedDegPerSec = 90f;
        [SerializeField] private float _minPitch = 20f;
        [SerializeField] private float _maxPitch = 80f;

        private Camera _camera;
        private bool _hasPrevTouchData;
        private Vector2 _prevTouchMidpoint;
        private float _prevTouchDistance;

        private Vector2 _prevMousePos;
        private bool _isMousePanning;

        private float _yaw;
        private float _pitch;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _yaw = transform.eulerAngles.y;
            _pitch = transform.eulerAngles.x;
        }

        private void Update()
        {
            if (PointerInput.TouchCount >= 2)
            {
                HandleTouchPanZoom();
            }
            else
            {
                _hasPrevTouchData = false;
                HandleMouseFallback();
            }

            HandleRotationInput();
            ClampPosition();
        }

        // Left/Right arrows and Q/D (AZERTY-friendly, same physical keys as
        // A/D) orbit the view around the ground point currently centered on
        // screen; Up/Down and Z/S tilt the pitch. Mirrors the orbit formula
        // GameBootstrap uses to place the camera initially (target - rotation
        // * forward * distance) so the look-at point stays fixed while orbiting.
        private void HandleRotationInput()
        {
            if (Keyboard.current == null)
            {
                return;
            }

            var yawInput = 0f;
            if (Keyboard.current.leftArrowKey.isPressed || Keyboard.current.qKey.isPressed)
            {
                yawInput -= 1f;
            }
            if (Keyboard.current.rightArrowKey.isPressed || Keyboard.current.dKey.isPressed)
            {
                yawInput += 1f;
            }

            var pitchInput = 0f;
            if (Keyboard.current.upArrowKey.isPressed || Keyboard.current.zKey.isPressed)
            {
                pitchInput -= 1f;
            }
            if (Keyboard.current.downArrowKey.isPressed || Keyboard.current.sKey.isPressed)
            {
                pitchInput += 1f;
            }

            if (yawInput == 0f && pitchInput == 0f)
            {
                return;
            }

            if (!TryGetGroundPivot(out var pivot, out var distance))
            {
                return;
            }

            _yaw += yawInput * _rotateSpeedDegPerSec * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch + pitchInput * _rotateSpeedDegPerSec * Time.deltaTime, _minPitch, _maxPitch);

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.rotation = rotation;
            transform.position = pivot - rotation * Vector3.forward * distance;
        }

        private bool TryGetGroundPivot(out Vector3 pivot, out float distance)
        {
            var pos = transform.position;
            var forward = transform.forward;
            if (Mathf.Abs(forward.y) < 0.0001f)
            {
                pivot = pos;
                distance = 0f;
                return false;
            }

            var t = -pos.y / forward.y;
            pivot = pos + forward * t;
            distance = Vector3.Distance(pos, pivot);
            return true;
        }

        private void HandleTouchPanZoom()
        {
            var touchA = PointerInput.GetTouchPosition(0);
            var touchB = PointerInput.GetTouchPosition(1);
            var midpoint = (touchA + touchB) * 0.5f;
            var distance = Vector2.Distance(touchA, touchB);

            if (_hasPrevTouchData)
            {
                var midpointDelta = midpoint - _prevTouchMidpoint;
                PanByScreenDelta(-midpointDelta);

                var distanceDelta = distance - _prevTouchDistance;
                Zoom(-distanceDelta * _zoomSpeed);
            }

            _prevTouchMidpoint = midpoint;
            _prevTouchDistance = distance;
            _hasPrevTouchData = true;
        }

        private void HandleMouseFallback()
        {
            if (Mouse.current == null)
            {
                return;
            }

            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                _isMousePanning = true;
                _prevMousePos = Mouse.current.position.ReadValue();
            }
            else if (Mouse.current.rightButton.wasReleasedThisFrame)
            {
                _isMousePanning = false;
            }
            else if (_isMousePanning)
            {
                var mousePos = Mouse.current.position.ReadValue();
                PanByScreenDelta(-(mousePos - _prevMousePos));
                _prevMousePos = mousePos;
            }

            // Input System reports the raw OS wheel delta (~120 per notch on
            // Windows) rather than the legacy ~0.1-per-notch normalized axis,
            // so it's rescaled here to land in roughly the same ballpark as
            // before — _mouseScrollZoomSpeed may still need re-tuning to feel
            // exactly the same as pre-migration.
            var scroll = Mouse.current.scroll.ReadValue().y / 120f;
            if (Mathf.Abs(scroll) > Mathf.Epsilon)
            {
                Zoom(-scroll * _mouseScrollZoomSpeed);
            }
        }

        private void PanByScreenDelta(Vector2 screenDelta)
        {
            var right = transform.right;
            var forward = Vector3.Cross(right, Vector3.up);
            var worldDelta = (right * screenDelta.x + forward * screenDelta.y) * (_panSpeed * _camera.orthographicSize * 0.002f);
            transform.position += worldDelta;
        }

        private void Zoom(float amount)
        {
            _camera.orthographicSize = Mathf.Clamp(_camera.orthographicSize + amount, _minOrthoSize, _maxOrthoSize);
        }

        private void ClampPosition()
        {
            var pos = transform.position;
            pos.x = Mathf.Clamp(pos.x, _panBoundsMin.x, _panBoundsMax.x);
            pos.z = Mathf.Clamp(pos.z, _panBoundsMin.y, _panBoundsMax.y);
            transform.position = pos;
        }

        public void SetPanBounds(Vector2 min, Vector2 max)
        {
            _panBoundsMin = min;
            _panBoundsMax = max;
        }
    }
}
