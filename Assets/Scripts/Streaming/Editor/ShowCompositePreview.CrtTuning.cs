#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Streaming.Cg;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class ShowCompositePreview
    {
        /// <summary>本編ステージから原板と透明 CG だけを借りる。post は調整卓が 1 回だけ掛ける。</summary>
        internal sealed class CrtTuningDollSource : IDisposable
        {
            private readonly Stage _stage;
            private readonly ShowJson _show;
            private readonly ShowActorDef _doll;
            private readonly Dictionary<string, Plate> _plates = new(StringComparer.Ordinal);
            private readonly Dictionary<string, PreviewCameraDef> _cameras = new(StringComparer.Ordinal);
            private readonly List<Texture2D> _textures = new();
            private readonly Dictionary<string, Vector3> _feet = new(StringComparer.Ordinal);
            private readonly Dictionary<string, float> _yaws = new(StringComparer.Ordinal);

            public CrtTuningDollSource()
            {
                string showPath = Path.Combine(Application.streamingAssetsPath, "show/show.json");
                _show = LoadShow(showPath, out _) ?? throw new FileNotFoundException("CRT doll show settings are missing", showPath);
                ShowLayoutDef layout = _show.layout ?? throw new InvalidOperationException("CRT doll layout is missing");
                if (!layout.hasRoom || layout.room == null || !layout.room.HasData() || layout.grid == null || !layout.grid.HasData())
                    throw new InvalidOperationException("CRT doll source requires authored room geometry and camera zones");
                _doll = _show.FindActor("doll") ?? throw new InvalidOperationException("CRT doll actor is missing");
                if (_doll.prefab != "ShowActors/Ichimatsu2" || Resources.Load<GameObject>(_doll.prefab) == null)
                    throw new InvalidOperationException("CRT doll source requires the production Ichimatsu2 prefab");
                try
                {
                    _stage = Stage.Create("");
                    _stage.ConfigureCrtTuning(layout, _show.feel);
                    foreach (string id in new[] { "A", "B", "C" })
                    {
                        PreviewCameraDef? camera = null;
                        foreach (PreviewCameraDef candidate in _show.cameras) if (candidate.id == id) { camera = candidate; break; }
                        if (camera == null) throw new InvalidOperationException("CRT doll camera is missing: " + id);
                        string path = PlateValidationLatestPlate(id);
                        var texture = new Texture2D(2, 2, TextureFormat.RGB24, true, false)
                        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                        _textures.Add(texture);
                        if (!texture.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("Invalid CRT doll plate: " + path);
                        var plate = new Plate { texture = texture, width = texture.width, height = texture.height, label = Path.GetFileName(path) };
                        Geometry geometry = _stage.AimVirtualCamera(camera, plate.width, plate.height);
                        RequireCalibration(geometry, id);
                        _plates.Add(id, plate); _cameras.Add(id, camera);
                        _stage.SetPlateLuma(plate);
                        Vector3 foot = _stage.SelectCrtTuningStand(_doll, layout, geometry, id[0] - 'A', out float yaw);
                        _feet.Add(id, foot); _yaws.Add(id, yaw);
                        Debug.Log($"[CrtTuning] CG doll {id} course=({foot.x - StageOriginX:0.000},{foot.y:0.000},{foot.z:0.000}) yaw={yaw:0.0}");
                    }
                }
                catch { Dispose(); throw; }
            }

            public float WriteTo(Material material, string camera)
            {
                Plate plate = _plates[camera];
                Geometry geometry = _stage.AimVirtualCamera(_cameras[camera], plate.width, plate.height);
                RequireCalibration(geometry, camera);
                return _stage.WriteCrtTuningLayers(material, plate, _doll, _feet[camera], _yaws[camera], geometry);
            }

            private static void RequireCalibration(Geometry geometry, string camera)
            {
                if (!geometry.usable || !geometry.fromCalib)
                    throw new InvalidOperationException("CRT doll source requires matching calibration for " + camera + ": " + geometry.label);
            }

            public void Dispose()
            {
                _stage?.Dispose();
                foreach (Texture2D texture in _textures) UnityEngine.Object.DestroyImmediate(texture);
                _textures.Clear(); _plates.Clear(); _cameras.Clear();
            }
        }

        private sealed partial class Stage
        {
            public void ConfigureCrtTuning(ShowLayoutDef layout, ShowFeelDef? feel)
            {
                // The tuner owns the visible screen; this stage supplies only its transparent CG target.
                _quad.gameObject.SetActive(false); _caption.gameObject.SetActive(false);
                ApplyLayout(layout); SetFeel(feel); ShowWire(false);
            }

            public Vector3 SelectCrtTuningStand(ShowActorDef doll, ShowLayoutDef layout, Geometry geometry, int zone, out float yaw)
            {
                float floor = layout.room!.floorY;
                Vector3 origin = new(StageOriginX, floor, 0);
                yaw = _cgCam.transform.eulerAngles.y + 180f;
                PlaceActorAtWorld(doll, origin, yaw, geometry);
                if (_actor == null || _rig == null || !_rig.HasRig)
                    throw new InvalidOperationException("CRT production doll rig did not initialize");
                Bounds bounds = PlateValidationBounds(PlateValidationVertices(_actor));
                origin.y += floor - bounds.min.y;
                PlaceActorAtWorld(doll, origin, yaw, geometry);
                List<Vector3> vertices = PlateValidationVertices(_actor);
                bounds = PlateValidationBounds(vertices);
                var navigation = new WalkNavigation(layout, 0.18f, 0.04f, 0f, doll.heightM);
                List<PlateValidationCandidate> candidates = PlateValidationCandidates(_cgCam, vertices,
                    PlateValidationRectHull(bounds), origin, doll.heightM, navigation, ShowRoomProxyLogic.Build(layout.room));
                ShowGridDef grid = layout.grid!;
                int[,] cells = WalkRouteLogic.BuildCells(grid, out int rows, out int cols);
                Vector2 best = default;
                float score = float.PositiveInfinity;
                foreach (PlateValidationCandidate candidate in candidates)
                {
                    int col = Mathf.FloorToInt(candidate.Position.x / grid.tileM + cols * 0.5f);
                    int row = Mathf.FloorToInt(rows * 0.5f - candidate.Position.y / grid.tileM);
                    if (row < 0 || row >= rows || col < 0 || col >= cols || cells[row, col] != zone) continue;
                    float value = (candidate.ImageBounds.center - new Vector2(0.5f, 0.5f)).sqrMagnitude;
                    if (value < score) { best = candidate.Position; score = value; }
                }
                if (float.IsInfinity(score)) throw new InvalidOperationException("No visible, grounded production doll position fits camera zone " + zone);
                return origin + new Vector3(best.x, 0, best.y);
            }

            public float WriteCrtTuningLayers(Material target, Plate plate, ShowActorDef doll, Vector3 foot, float yaw, Geometry geometry)
            {
                SetPlateLuma(plate);
                PlaceActorAtWorld(doll, foot, yaw, geometry);
                if (_actor == null) throw new InvalidOperationException("CRT doll actor is missing");
                foreach (MonoBehaviour behaviour in _actor.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                _cgCam.Render();
                float aspect = (float)plate.width / plate.height;
                float frameAspect = CrtReferenceScreen.LogicalAspect;
                Vector2 contain = aspect < frameAspect ? new Vector2(aspect / frameAspect, 1) : new Vector2(1, frameAspect / aspect);
                target.SetTexture("_LiveTex", plate.texture);
                target.SetTexture("_CgTex", _cgCam.targetTexture); target.SetFloat("_CgStrength", 1);
                target.SetVector("_LiveScale", new Vector4(contain.x, contain.y, 0, 0));
                target.SetVector("_CgScale", new Vector4(contain.x, contain.y, 0, 0));
                target.SetVector("_CgLens", _compositeMat.GetVector("_CgLens"));
                target.SetVector("_CgFocalN", _compositeMat.GetVector("_CgFocalN"));
                Vector4 light = new(0.5f, 0.5f, 0.2f, 0);
                ShowCgLayer.TryActorLight(_cgCam, _actor.transform.position, _actorHeightM, contain, ShowCgLayer.ActorLightEv, out light);
                target.SetVector("_ActorLight", light);
                target.SetVector("_RedKeep", new Vector4(0, ShowCgLayer.DollRedKeep, 0, 0));
                return _feelLogic.SteadyBiasFor(_plateLuma) * _feel.Agc;
            }
        }
    }
}
