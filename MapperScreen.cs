using UnityEngine;
using UnityEngine.AI;

namespace RestoreMapper
{
    public class MapperScreen : MonoBehaviour
    {
        public Camera cam;
        public Light light;
        public Renderer transition;
        public Transform target;
        public LineRenderer line;
        public bool drawPath;

        NavMeshPath path;
        Canvas mapperCanvas;
        float ratio = 1f;

        void Awake()
        {
            if (cam == null)
                cam = GetComponent<Camera>();

            path = new();

            // create a new canvas so the compass rose can be displayed on the mapper
            SetupCanvas();

            // create a new pathing line
            SetupLine();
        }

        void LateUpdate()
        {
            if (target == null)
                return;

            Vector3 followPos = target.position;
            followPos.y += 3.636f;
            transform.position = followPos;
        }

        void OnDisable()
        {
            target = null;
            drawPath = false;
        }

        void OnDestroy()
        {
            if (mapperCanvas != null)
                Destroy(mapperCanvas.gameObject);
        }

        void OnEnable()
        {
            if (target == null || target.position.y >= -80f || !Plugin.configShowPath.Value)
                return;

            bool mineshaft = RoundManager.Instance.currentDungeonType == 4 && RoundManager.Instance.currentMineshaftElevator != null;
            if (mineshaft && Vector3.Distance(target.position, RoundManager.Instance.currentMineshaftElevator.elevatorInsidePoint.position) <= 1f)
                return;

            if (!NavMesh.CalculatePath(target.position, (mineshaft && !RestoreMapperPatches.mineStartBounds.Contains(target.position)) ? RoundManager.Instance.currentMineshaftElevator.elevatorBottomPoint.position : RestoreMapperPatches.mainEntrancePos, NavMesh.AllAreas, path) || path.corners.Length < 2)
                return;

            Vector3[] points = new Vector3[path.corners.Length];
            for (int i = 0; i < path.corners.Length; i++)
                points[i] = path.corners[i] + (Vector3.up * 1.25f);

            line.positionCount = Mathf.Min(points.Length, 50);
            line.SetPositions(points);

            drawPath = true;
        }

        void SetupCanvas()
        {
            // clone the original radar's UI
            mapperCanvas = Instantiate(StartOfRound.Instance.radarCanvas.gameObject, StartOfRound.Instance.radarCanvas.transform.parent, true).GetComponent<Canvas>();

            // remove everything except the compass rose
            foreach (Transform child in mapperCanvas.transform)
            {
                if (child.name == "MonitoringPlayerUIContainer")
                {
                    foreach (Transform grandchild in child)
                    {
                        if (grandchild.name == "CompassRose")
                        {
                            grandchild.gameObject.SetActive(true);
                            continue;
                        }

                        Destroy(grandchild.gameObject);
                    }

                    child.gameObject.SetActive(true);
                    continue;
                }

                Destroy(child.gameObject);
            }

            // assign it to render on the right camera
            mapperCanvas.worldCamera = GetComponent<Camera>();

            // adjust the scale to match the screen size
            RenderTexture orig = StartOfRound.Instance.mapScreen.cam.targetTexture;
            ratio = (((float)cam.targetTexture.width / orig.width) + ((float)cam.targetTexture.height / orig.height)) / 2f;
            if (ratio != 1f)
                mapperCanvas.scaleFactor *= ratio;
        }

        void SetupLine()
        {
            line = Instantiate(StartOfRound.Instance.mapScreen.lineFromRadarTargetToExit.gameObject, StartOfRound.Instance.mapScreen.lineFromRadarTargetToExit.transform.parent, true).GetComponent<LineRenderer>();
            line.forceRenderingOff = true;
            line.material.SetTextureOffset("_MainTex", Vector2.zero);
            line.enabled = true;
        }
    }
}
