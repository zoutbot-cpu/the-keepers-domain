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

        private Camera _camera;
        private bool _hasPrevTouchData;
        private Vector2 _prevTouchMidpoint;
        private float _prevTouchDistance;

        private Vector2 _prevMousePos;
        private bool _isMousePanning;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
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

            ClampPosition();
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
