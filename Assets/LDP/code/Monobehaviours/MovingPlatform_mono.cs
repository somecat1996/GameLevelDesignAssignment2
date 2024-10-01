using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DevDev.LDP
{
    public class MovingPlatform_mono : MonoBehaviour
    {
#if UNITY_EDITOR
        public bool alwaysDrawGizmos = true;
#endif
        public MovingPlatform data;

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            bool isSelected = false;
            Transform[] selectedTransforms = Selection.transforms;
            for (int i = 0; i < selectedTransforms.Length; i++)
            {
                Transform t = selectedTransforms[i];
                if (t == transform || t.IsChildOf(transform))
                {
                    isSelected = true;
                    break;
                }
            }

            // Draw only when this object or one of its children is selected
            if (!alwaysDrawGizmos && !isSelected)
                return;


            if(isSelected)
                Gizmos.color = Color.green;
            else 
                Gizmos.color = Color.Lerp(Color.green, Color.blue, .6f);


            //Move platform to starting position
            if (!Application.isPlaying && data.platform != null)
                data.platform.position = Procs.MovingPlatform_EvaluatePosition(data, data.loopOffset);

            switch (data.trackType)
            {
                case PlatformTrackType.CIRCULAR:
                    {
                        if (data.pingpong_waypoint_0 != null && data.pingpong_waypoint_1 != null)
                        {
                            data.pingpong_waypoint_0.gameObject.SetActive(false);
                            data.pingpong_waypoint_1.gameObject.SetActive(false);
                        }
                        Procs.GizmosDrawXYCircle(transform.position, data.circular_radius, 10);
                    }
                    break;
                case PlatformTrackType.PINGPONG:
                    {
                        if (data.pingpong_waypoint_0 != null && data.pingpong_waypoint_1 != null)
                        {
                            data.pingpong_waypoint_0.gameObject.SetActive(true);
                            data.pingpong_waypoint_1.gameObject.SetActive(true);

                            Gizmos.DrawLine(data.pingpong_waypoint_0.position, data.pingpong_waypoint_1.position);
                            if (data.platform != null)
                            {
                                Collider2D col = data.platform.GetComponent<Collider2D>();
                                if (col != null)
                                {
                                    // Force the collider to update with latest platform position. This avoids jitter when moving waypoints.
                                    col.enabled = false;
                                    col.enabled = true;

                                    Mesh colMesh = col.CreateMesh(false, false);
                                    colMesh.RecalculateNormals();
                                    Gizmos.DrawWireMesh(colMesh, data.pingpong_waypoint_0.position - data.platform.position);
                                    Gizmos.DrawWireMesh(colMesh, data.pingpong_waypoint_1.position - data.platform.position);
                                }
                            }
                        }
                    }
                    break;
            }
        }
#endif
    }
}
