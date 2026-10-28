using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// アバターを後ろから追いかける三人称カメラ。
/// - 画面を左クリックでマウスをロックして視点操作、Esc で解除
/// - ロックしていないときは、Cesium のクレジット表示（Data Attribution など）をクリックできる
/// - ホイールで距離を変更
/// - 建物にめり込まないように距離を縮める
/// Main Camera に付けて、target にアバターを指定する。
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;

    [Header("Orbit")]
    public float distance = 4f;
    public float minDistance = 1f;
    public float maxDistance = 15f;
    public float pivotHeight = 1.4f;      // アバターの頭あたり
    [Tooltip("マウス感度 (度/ピクセル)")]
    public float sensitivity = 0.15f;
    public float minPitch = -30f;
    public float maxPitch = 70f;
    public float zoomSpeed = 1f;

    [Header("Collision")]
    public float collisionRadius = 0.2f;
    [Tooltip("アバターを専用レイヤーにして、ここから外すとより安定する")]
    public LayerMask collisionMask = ~0;

    // Cesium のクレジット表示の要素名。この上でのクリックではマウスをロックしない
    static readonly string[] CreditElementNames = { "OnScreenCredits", "PopupCredits" };

    float yaw;
    float pitch = 15f;

    void Start()
    {
        if (target) yaw = target.eulerAngles.y;
    }

    void LateUpdate()
    {
        if (!target) return;

        HandleCursorLock();

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 look = ReadLook();
            yaw += look.x * sensitivity;
            pitch -= look.y * sensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        distance = Mathf.Clamp(distance - ReadScroll() * zoomSpeed, minDistance, maxDistance);

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pivot = target.position + Vector3.up * pivotHeight;
        Vector3 dir = rot * Vector3.back;

        float d = distance;
        if (Physics.SphereCast(pivot, collisionRadius, dir, out var hit, distance, collisionMask, QueryTriggerInteraction.Ignore))
        {
            d = Mathf.Max(hit.distance, 0.3f);
        }

        transform.SetPositionAndRotation(pivot + dir * d, rot);
    }

    void HandleCursorLock()
    {
        bool clicked;
        bool escape;
        Vector2 mousePos;
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        var k = Keyboard.current;
        clicked = m != null && m.leftButton.wasPressedThisFrame;
        escape = k != null && k.escapeKey.wasPressedThisFrame;
        mousePos = m != null ? m.position.ReadValue() : Vector2.zero;
#else
        clicked = Input.GetMouseButtonDown(0);
        escape = Input.GetKeyDown(KeyCode.Escape);
        mousePos = Input.mousePosition;
#endif
        if (escape) Lock(false);

        if (clicked && Cursor.lockState != CursorLockMode.Locked && !IsPointerOverCredits(mousePos))
        {
            Lock(true);
        }
    }

    /// <summary>
    /// 画面上の位置に Cesium のクレジット表示があるか。
    /// </summary>
    static bool IsPointerOverCredits(Vector2 screenPos)
    {
        // UI Toolkit の座標は左上が原点なので Y を反転する
        Vector2 flipped = new Vector2(screenPos.x, Screen.height - screenPos.y);

        // クレジット表示はどのシーンにも属さない非表示オブジェクトとして作られるため、
        // FindObjectsByType ではなく Resources.FindObjectsOfTypeAll で探す
        foreach (var doc in Resources.FindObjectsOfTypeAll<UIDocument>())
        {
            if (doc == null || !doc.isActiveAndEnabled) continue;
            var panel = doc.rootVisualElement?.panel;
            if (panel == null) continue;

            VisualElement picked = panel.Pick(RuntimePanelUtils.ScreenToPanel(panel, flipped));
            for (var e = picked; e != null; e = e.parent)
            {
                if (System.Array.IndexOf(CreditElementNames, e.name) >= 0) return true;
            }
        }
        return false;
    }

    static void Lock(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    Vector2 ReadLook()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
        // 旧 Input Manager の Mouse X/Y はピクセル×0.1 なので 10 倍してそろえる
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
#endif
    }

    float ReadScroll()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null) return 0f;
        float y = Mouse.current.scroll.ReadValue().y;
        return Mathf.Clamp(y / 120f, -1f, 1f); // 1 ノッチ ≒ 1
#else
        return Input.mouseScrollDelta.y;
#endif
    }
}
