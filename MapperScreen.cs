using UnityEngine;

namespace RestoreMapper
{
    public class MapperScreen : MonoBehaviour
    {
        public Light light;
        public Renderer transition;
        public Transform target;

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
        }
    }
}
