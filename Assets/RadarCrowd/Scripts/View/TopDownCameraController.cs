using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class TopDownCameraController : MonoBehaviour
{
    [SerializeField] private bool initializeOnStart = true;
    [SerializeField] private bool frameSceneOnStart = true;
    [SerializeField] private Vector3 fallbackFocusPoint = new Vector3(40f, 0f, -40f);
    [SerializeField] private float startHeight = 45f;
    [SerializeField, Range(35f, 80f)] private float pitch = 60f;
    [SerializeField] private float yaw = 0f;
    [SerializeField] private float moveSpeed = 25f;
    [SerializeField] private float fastMoveMultiplier = 2.5f;

    private void Start()
    {
        if (!initializeOnStart)
        {
            return;
        }

        Vector3 focusPoint = frameSceneOnStart ? FindSceneFocusPoint() : fallbackFocusPoint;
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 0.0001f)
        {
            flatForward = Vector3.forward;
        }

        float distanceFromFocus = startHeight / Mathf.Tan(pitch * Mathf.Deg2Rad);
        transform.position = focusPoint - flatForward * distanceFromFocus + Vector3.up * startHeight;
    }

    private void Update()
    {
        Vector2 input = ReadMoveInput();
        if (input.sqrMagnitude < 0.0001f)
        {
            return;
        }

        input = Vector2.ClampMagnitude(input, 1f);

        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
        float speed = moveSpeed * (IsFastMovePressed() ? fastMoveMultiplier : 1f);

        transform.position += (right * input.x + forward * input.y) * speed * Time.unscaledDeltaTime;
    }

    private Vector3 FindSceneFocusPoint()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        Bounds bounds = new Bounds(fallbackFocusPoint, Vector3.zero);
        bool hasBounds = false;

        foreach (Renderer sceneRenderer in renderers)
        {
            if (sceneRenderer == null || !sceneRenderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = sceneRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(sceneRenderer.bounds);
            }
        }

        Vector3 center = hasBounds ? bounds.center : fallbackFocusPoint;
        center.y = 0f;
        return center;
    }

    private static Vector2 ReadMoveInput()
    {
        Vector2 input = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                input.x -= 1f;
            }

            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                input.x += 1f;
            }

            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            {
                input.y -= 1f;
            }

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            {
                input.y += 1f;
            }

            return input;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        input.x = Input.GetAxisRaw("Horizontal");
        input.y = Input.GetAxisRaw("Vertical");
#endif

        return input;
    }

    private static bool IsFastMovePressed()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            return keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#else
        return false;
#endif
    }
}
