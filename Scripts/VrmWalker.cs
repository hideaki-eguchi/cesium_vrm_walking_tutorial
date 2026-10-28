using UnityEngine;
using CesiumForUnity;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Cesium の地形・建物の上を CharacterController で歩かせるための最小スクリプト。
/// - 起動直後はタイルのコライダーが読み込まれるまで待ち、地面を見つけてから着地させる
/// - WASD で移動（カメラの向き基準）、Shift で走る、Space でジャンプ
/// - 地面を突き抜けて落ちたら、最後に立っていた場所に戻す
/// 前提: CesiumGeoreference の原点がアバターの近く（数 km 以内）にあり、Unity の +Y がほぼ「上」になっていること
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class VrmWalker : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 2.0f;
    public float runSpeed = 5.0f;
    public float rotationSpeed = 720f;   // 向きを変える速さ (度/秒)
    public float jumpHeight = 1.2f;
    public float gravity = -9.81f;

    [Header("References")]
    [Tooltip("移動方向の基準にするカメラ。空なら Main Camera を使う")]
    public Transform cameraTransform;
    [Tooltip("任意。float パラメータ \"Speed\" があれば移動速度を渡す")]
    public Animator animator;

    [Header("Spawn / Safety")]
    [Tooltip("読み込み完了を待つタイルセット（地形と建物）。空なら待たない")]
    public Cesium3DTileset[] waitForTilesets;
    [Tooltip("タイル読み込み待ちの最大秒数")]
    public float spawnTimeout = 20f;
    [Tooltip("着地点を探すとき、現在位置からどれだけ上からレイを撃つか (m)")]
    public float groundProbeUp = 200f;
    public float groundProbeDistance = 2000f;
    public LayerMask groundMask = ~0;
    [Tooltip("この速さ以上で落下し続けたら地面を突き抜けたとみなして戻す (m/s)")]
    public float fallResetSpeed = 50f;
    [Tooltip("地面にめり込んだら自動で地上に押し戻す")]
    public bool autoUnstuck = true;

    CharacterController cc;
    float verticalVelocity;
    bool spawned;
    float spawnWaitTime;
    bool hasSpeedParam;
    Vector3 lastGroundedPosition;
    static readonly int SpeedHash = Animator.StringToHash("Speed");

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        cc.enabled = false; // 地面が見つかるまで無効
        if (!animator) animator = GetComponentInChildren<Animator>();
        if (animator)
        {
            foreach (var p in animator.parameters)
                if (p.nameHash == SpeedHash && p.type == AnimatorControllerParameterType.Float) hasSpeedParam = true;
        }
        lastGroundedPosition = transform.position;
    }

    void Start()
    {
        if (!cameraTransform && Camera.main) cameraTransform = Camera.main.transform;
    }

    void Update()
    {
        if (!spawned)
        {
            TrySpawn(transform.position);
            return;
        }

        // --- 入力 ---
        Vector2 input = ReadMove();
        bool run = ReadRun();
        bool jump = ReadJumpDown();

        // --- カメラ基準の移動方向 ---
        Vector3 up = Vector3.up;
        Vector3 fwd = cameraTransform
            ? Vector3.ProjectOnPlane(cameraTransform.forward, up).normalized
            : transform.forward;
        Vector3 right = Vector3.Cross(up, fwd);
        Vector3 move = fwd * input.y + right * input.x;
        if (move.sqrMagnitude > 1f) move.Normalize();

        float speed = run ? runSpeed : walkSpeed;

        if (move.sqrMagnitude > 0.0001f)
        {
            Quaternion target = Quaternion.LookRotation(move, up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, rotationSpeed * Time.deltaTime);
        }

        // --- 重力・ジャンプ ---
        if (cc.isGrounded)
        {
            lastGroundedPosition = transform.position;
            if (verticalVelocity < 0f) verticalVelocity = -2f; // 地面に押し付けておく
            if (jump) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
        verticalVelocity += gravity * Time.deltaTime;

        cc.Move((move * speed + up * verticalVelocity) * Time.deltaTime);

        // 0.1 秒かけてなめらかに変化させる（歩き⇔走りの切り替えが自然になる）
        if (hasSpeedParam) animator.SetFloat(SpeedHash, move.magnitude * speed, 0.1f, Time.deltaTime);

        // --- 突き抜け対策 ---
        if (verticalVelocity < -fallResetSpeed)
        {
            Respawn(lastGroundedPosition);
            return;
        }

        // --- めり込み対策 ---
        // 詳細なタイルに切り替わって地面が足より上に来たら、地上に押し戻す
        if (autoUnstuck && IsBuried(out float groundY))
        {
            cc.enabled = false;
            var p = transform.position;
            transform.position = new Vector3(p.x, groundY + 0.05f, p.z);
            verticalVelocity = 0f;
            cc.enabled = true;
        }
    }

    bool TilesetsReady()
    {
        if (waitForTilesets == null || waitForTilesets.Length == 0) return true;
        foreach (var t in waitForTilesets)
        {
            if (t != null && t.isActiveAndEnabled && t.ComputeLoadProgress() < 99.9f) return false;
        }
        return true;
    }

    void TrySpawn(Vector3 around)
    {
        spawnWaitTime += Time.deltaTime;
        // 粗いタイルのうちに着地すると、詳細なタイルが来たときに地面の中に埋まるので、読み込み完了まで待つ
        if (!TilesetsReady() && spawnWaitTime < spawnTimeout) return;

        // 上空から下向きに全ヒットを取り、置いた高さに一番近い面を地面とみなす
        // （ビルの屋上ではなく足元の地面を選ぶため。埋まっていても上の地表が見つかる）
        Vector3 origin = around + Vector3.up * groundProbeUp;
        var hits = Physics.RaycastAll(origin, Vector3.down, groundProbeDistance, groundMask, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0) return; // 次のフレームで再挑戦

        RaycastHit best = hits[0];
        float bestDiff = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == cc) continue;
            float diff = Mathf.Abs(h.point.y - around.y);
            if (diff < bestDiff) { bestDiff = diff; best = h; }
        }
        if (bestDiff == float.MaxValue) return;

        transform.position = best.point + Vector3.up * 0.05f;
        verticalVelocity = 0f;
        cc.enabled = true;
        spawned = true;
        lastGroundedPosition = transform.position;
    }

    bool IsBuried(out float groundY)
    {
        groundY = 0f;
        // 頭の少し下から足元に向けてレイを撃ち、足より上に面があれば埋まっている
        Vector3 feet = transform.position;
        float probe = Mathf.Max(0.5f, cc.height * 0.8f);
        Vector3 origin = feet + Vector3.up * probe;
        if (Physics.Raycast(origin, Vector3.down, out var hit, probe, groundMask, QueryTriggerInteraction.Ignore)
            && hit.collider != cc
            && hit.point.y > feet.y + 0.15f)
        {
            groundY = hit.point.y;
            return true;
        }
        return false;
    }

    public void Respawn(Vector3 around)
    {
        cc.enabled = false;
        spawned = false;
        spawnWaitTime = 0f;
        transform.position = around;
        verticalVelocity = 0f;
    }

    // ---------- Input (新 Input System / 旧 Input Manager 両対応) ----------
    Vector2 ReadMove()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == null) return Vector2.zero;
        float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
        float y = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
        return new Vector2(x, y);
#else
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
    }

    bool ReadRun()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        return k != null && k.leftShiftKey.isPressed;
#else
        return Input.GetKey(KeyCode.LeftShift);
#endif
    }

    bool ReadJumpDown()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        return k != null && k.spaceKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
#endif
    }
}
