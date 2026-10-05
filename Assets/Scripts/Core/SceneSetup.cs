using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.UI;

namespace KeepersDomain.Core
{
    /// Scene plumbing every world-building path shares: cameras, the sun, and
    /// named component GameObjects.
    internal static class SceneSetup
    {
        /// GameBootstrap owns the one true camera/listener for this prototype.
        /// Whatever scene happens to be loaded may already have its own default
        /// Main Camera (and AudioListener) — clear those out first so Unity's
        /// "multiple audio listeners" warning can't spam the console.
        internal static void RemoveStrayCameras()
        {
            foreach (var existingCamera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude))
            {
                Object.Destroy(existingCamera.gameObject);
            }
        }

        internal static void CreateSun()
        {
            // Near-straight-down key light so every tile across the map is
            // lit the same amount — a shallower angle throws long wall
            // shadows that streak unevenly across the floor. Kept a few
            // degrees off vertical (and slightly off-axis) so the KayKit
            // wall meshes still get a touch of face shading instead of
            // reading perfectly flat.
            var lightGO = new GameObject("Sun");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;
            light.shadowStrength = 0.45f;
            lightGO.transform.rotation = Quaternion.Euler(78f, -18f, 0f);

            // Set explicitly rather than trusting the scene file's own
            // Render Settings — Prototype.unity's Ambient Mode is left at
            // Skybox with no Skybox Material assigned, which leaves
            // surfaces with no ambient/fill light at all. That's invisible
            // on the flat-topped placeholder cubes (their top face still
            // catches the Sun directly), but it reads as solid black on
            // the KayKit wall meshes' vertical faces wherever they don't
            // point straight at the Sun. Flat ambient is the simplest fix
            // that doesn't depend on a skybox existing — pushed brighter
            // here so shadowed/side faces stay clearly readable and the
            // whole dungeon sits at a higher overall light level.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.68f);
        }

        /// panMargin is the caller's own choice rather than a fixed
        /// constant — BuildWorld's gameplay grid is always the same fixed
        /// size, but the level designer's map varies a lot (12 to 256 per
        /// side — see LevelDesignerPropertiesMenu), so its own caller
        /// (BuildLevelDesignerWorld) scales the margin to the actual map
        /// footprint instead of reusing gameplay's fixed 22.5.
        ///
        /// focusGroundPoint is the ground position the view opens centered
        /// on — the local player's Throne Room in gameplay (see BuildWorld),
        /// so each player starts looking at their own dungeon rather than
        /// the geometric middle of the map, which on a multi-player level
        /// is nobody's. Null centers on the map middle, the original
        /// behavior (still used by the Level Designer preview). Pan bounds
        /// stay anchored to the map middle either way, so opening off-center
        /// never shrinks how far the camera can roam.
        internal static Camera CreateIsoCamera(DungeonGrid grid, float panMargin, Vector3? focusGroundPoint = null)
        {
            var cameraGO = new GameObject("Main Camera");
            cameraGO.tag = "MainCamera";
            var camera = cameraGO.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            cameraGO.AddComponent<AudioListener>();

            var mapCenter = new Vector3(grid.Width * grid.CellSize * 0.5f, 0f, grid.Height * grid.CellSize * 0.5f);
            var target = focusGroundPoint ?? mapCenter;
            var rotation = Quaternion.Euler(45f, 45f, 0f);
            const float distance = 20f;
            cameraGO.transform.rotation = rotation;
            cameraGO.transform.position = target - rotation * Vector3.forward * distance;

            var isoCam = cameraGO.AddComponent<IsoCameraController>();
            // Bounds are anchored to the map-center camera position, never
            // the (possibly off-center) opening position — panMargin is
            // sized by the caller to be at least half the map footprint
            // plus slack (see both call sites), so center ± panMargin
            // already reaches every edge tile no matter where the view
            // opens. Deriving bounds from the opening position instead
            // would let a Throne-Room-focused start cut off the far side of
            // the map.
            var mapCenterCamPos = mapCenter - rotation * Vector3.forward * distance;
            isoCam.SetPanBounds(
                new Vector2(mapCenterCamPos.x - panMargin, mapCenterCamPos.z - panMargin),
                new Vector2(mapCenterCamPos.x + panMargin, mapCenterCamPos.z + panMargin));

            return camera;
        }

        internal static T CreateComponent<T>(string name) where T : Component
        {
            return CreateComponent<T>(name, parent: null);
        }

        internal static T CreateComponent<T>(string name, Transform parent) where T : Component
        {
            var go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, worldPositionStays: false);
            }
            return go.AddComponent<T>();
        }
    }
}
