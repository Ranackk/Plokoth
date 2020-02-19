using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum CameraModes
{
	FocusPlayer                 = 0,
	TransitionToAnchorZone      = 1,
	TransitionToFocusPlayer     = 2,
	LockOnPlayer                = 3,
}

public class CameraManager : IManager
{
	[SerializeField] private GameObject         m_CameraHolder;
	[SerializeField] private Camera             m_Camera;
	[SerializeField] private MeshRenderer       m_ScreenEffectRenderer;

	[SerializeField] private CameraModes        m_CameraMode;

	[SerializeField] private CameraAnchorZone   m_CurrentAnchorZone = null;
	//[SerializeField] private bool               m_OverlapsAnchorCollider = false;
	
	// Scale & Movement
	private float               m_BaseSizeY;
	private float               m_BaseSizeX;
	private float               m_ZoomFactor;
	
	private float               m_DesiredScaleFactor;
	private float               m_PreviousScaleFactor;
	private float               m_ZoomTimeRemaining;

	private Vector2             m_FocusPosition;
	private Vector2             m_LastLerpedCameraMove = Vector2.right;
	private bool                m_CollidedXLastTick = false;
	private bool                m_CollidedYLastTick = false;

	// Shake
	private float               m_CurrentTrauma             = 0.0f;
	private float               m_CurrentSmoothedTrauma     = 0.0f;

	// Cache
	const int MAX_RACAST_RESULTS = 8;
	private RaycastHit2D[]      m_RaycastHits           = new RaycastHit2D[MAX_RACAST_RESULTS]; 
	private int                 m_RaycastHitCount       = 0; 

	private Collider2D[]        m_ColliderResults       = new Collider2D[MAX_RACAST_RESULTS]; 
	private int                 m_ColliderResultCount   = 0; 

	// DEBUG

	private int                 m_TickEvery = 1;

	public bool                 DoDebugRenderering { get { return m_DoDebugRendering; } }
	private bool                m_DoDebugRendering = false;

	public bool                 m_DoCameraLogic = true;

    [ExposeToLua]
    public static void SetFreezeCamera(bool value)
    {
        Get().m_DoCameraLogic = !value;
    }

	[ExposeToLua]
	public static void SetUpdateCameraEvery(int value)
	{
		Get().m_TickEvery = value;
	}
	
	[ExposeToLua]
	public static void SetCameraDebugRendering(bool value)
	{
		Get().m_DoDebugRendering = value;
	}
	
	[ExposeToLua]
	public static void SetCameraLocked(bool value)
	{
		if (value)
		{
			Get().SetCameraMode(CameraModes.LockOnPlayer);
		}
		else
		{
			Get().SetCameraMode(CameraModes.TransitionToFocusPlayer);
		}
	}


	////////////////////////////////////////////////////////////////

	public static CameraManager Get()
	{
		return (CameraManager) GameCore.Get().GetManager(ManagerType.Camera);
	}
	
	////////////////////////////////////////////////////////////////
	
	public Camera GetCamera()
	{
		return m_Camera;
	}

	////////////////////////////////////////////////////////////////
	
	public MeshRenderer GetEffectRenderer()
	{
		return m_ScreenEffectRenderer;
	}

	////////////////////////////////////////////////////////////////

	public override void Initialize()
	{
		JSDK.Events.EventManager.Get().AddListener<PlayerResetEvent>(OnPlayerReset);
		JSDK.Events.EventManager.Get().AddListener<PlayerDeathEvent>(OnPlayerDeath);
		
		m_CameraMode        = CameraModes.FocusPlayer;
		m_CurrentAnchorZone = null;

		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();

		{
			SceneContext context        = SceneContext.MetaContext();
			m_Camera                    = Camera.main;
			m_CameraHolder              = m_Camera.transform.parent.gameObject;
			m_BaseSizeY                 = assetDataCamera.SizeY / 2.0f;
			m_BaseSizeX                 = m_BaseSizeY * Screen.width / (float) Screen.height;
			SetCameraZoom(1.0f);

			m_ScreenEffectRenderer                          = m_CameraHolder.GetComponentInChildren<MeshRenderer>();
			m_ScreenEffectRenderer.transform.position       = m_ScreenEffectRenderer.transform.position.xy().To3D_Z();
			context.Unset();
		}
	}

	////////////////////////////////////////////////////////////////

	public override void Uninitialize()
	{
		JSDK.Events.EventManager.Get().RemoveListener<PlayerResetEvent>(OnPlayerReset);
		JSDK.Events.EventManager.Get().RemoveListener<PlayerDeathEvent>(OnPlayerDeath);
	}
	
	////////////////////////////////////////////////////////////////
	
	void OnPlayerDeath(PlayerDeathEvent e)
	{
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		AddTrauma(assetDataCamera.TraumaOnPlayerDeath);
	}

	////////////////////////////////////////////////////////////////
	
	void OnPlayerReset(PlayerResetEvent e)
	{
        if (!m_DoCameraLogic)
        {
            return;
        }

		PlayerController player         = PlayerManager.Get().GetPlayerController();
		m_DesiredScaleFactor            = 1.0f;
		SetCameraZoom(m_DesiredScaleFactor);
		m_ZoomTimeRemaining             = 0.0f;

		Vector2 newCameraPosition;

		////////////////////////////////////////////////////////////////
		
		CameraAnchorZone anchorZone     = GetCameraAnchorZoneIfAny(player.transform.position.xy(), player.Collider != null ? player.Collider.radius + 0.1f : 0.1f);
		
		if (anchorZone)
		{
			// Prio 1: We focus the the newly entered camera anchor zone.
			if (anchorZone != m_CurrentAnchorZone)
			{
				OnPlayerEnteredAnchorZone(anchorZone);
				//return;
			}
			else
			{
				SetCameraMode(CameraModes.TransitionToAnchorZone);
			}
				
			FocusCameraOn(anchorZone.GetTargetCameraPosition(GetCameraColliderSize(), player.transform.position));

			m_DesiredScaleFactor    = m_CurrentAnchorZone.CameraZoom;
			SetCameraZoom(m_DesiredScaleFactor);
			m_ZoomTimeRemaining     = 0.0f;

			newCameraPosition       = m_CameraHolder.transform.position;
		}
		else
		{
			if (m_CurrentAnchorZone != null)
			{
				m_CurrentAnchorZone.SetCameraCollidersActive(false);
				m_CurrentAnchorZone = null;
			}
			
			SetCameraMode(CameraModes.FocusPlayer);

			// Prio 2: We focus the player

			newCameraPosition       = player.transform.position.xy();
		}
		
		///////////////////////////////////////////////////////////////
		// Collision Checks
		
		float paddingY = m_BaseSizeY * 1.0f / m_ZoomFactor;
		float paddignX = m_BaseSizeX * 1.0f / m_ZoomFactor;

		// Check Top
		m_RaycastHitCount           = Physics2D.RaycastNonAlloc(newCameraPosition, Vector2.down, m_RaycastHits, paddingY, (int) CommonLayerMasks.Camera);
		int? colliderIndex          = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, false);
		if (colliderIndex.HasValue)
		{
			newCameraPosition.y = m_RaycastHits[colliderIndex.Value].point.y + paddingY;
		}
		
		// Check Sides
		m_RaycastHitCount           = Physics2D.RaycastNonAlloc(newCameraPosition, Vector2.right, m_RaycastHits, paddignX, (int) CommonLayerMasks.Camera);
		colliderIndex               = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, false);
		if (colliderIndex.HasValue)
		{
			float directionFactor = ((m_RaycastHits[colliderIndex.Value].point.x < newCameraPosition.x) ? 1.0f : -1.0f);
			newCameraPosition.x = m_RaycastHits[colliderIndex.Value].point.x + directionFactor * paddignX;
		}

		m_RaycastHitCount           = Physics2D.RaycastNonAlloc(newCameraPosition, Vector2.left, m_RaycastHits,paddignX, (int) CommonLayerMasks.Camera);
		colliderIndex               = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, false);
		if (colliderIndex.HasValue)
		{
			float directionFactor = ((m_RaycastHits[colliderIndex.Value].point.x < newCameraPosition.x) ? 1.0f : -1.0f);
			newCameraPosition.x = m_RaycastHits[colliderIndex.Value].point.x + directionFactor * paddignX;
		}

		FocusCameraOn(newCameraPosition);
	}

	////////////////////////////////////////////////////////////////
	
	void TickCameraScale()
	{
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		if (m_ZoomTimeRemaining > 0.0f)
		{
			m_ZoomTimeRemaining -= Time.fixedDeltaTime;

			float newScaleFactor = Mathf.LerpAngle(m_PreviousScaleFactor, m_DesiredScaleFactor, 1.0f - (m_ZoomTimeRemaining / assetDataCamera.ZoomTime));
			SetCameraZoom(newScaleFactor);
		}
	}
	
	////////////////////////////////////////////////////////////////
	
	public void SetCameraZoom(float zoomFactor)
	{
		m_ZoomFactor                = zoomFactor;
		m_Camera.orthographicSize   = m_BaseSizeY / m_ZoomFactor;
	}

	////////////////////////////////////////////////////////////////
	
	CameraAnchorZone GetCameraAnchorZoneIfAny(Vector2 position, float radius)
	{
		Collider2D cameraAnachorZoneCollider = Physics2D.OverlapCircle(position, radius, (int) CommonLayerMasks.Camera);
		if (!cameraAnachorZoneCollider)
		{
			return null;
		}

		return cameraAnachorZoneCollider.GetComponentInParent<CameraAnchorZone>();
	}
	
	////////////////////////////////////////////////////////////////

	bool IsCameraAnchored()
	{
		return m_CurrentAnchorZone != null;
	}
	
	////////////////////////////////////////////////////////////////

	void DebugTickParallax()
	{
		if (Input.GetKeyDown(KeyCode.Alpha0))
		{
			m_Camera.transform.position = new Vector3(0, 100, m_Camera.transform.position.z);
		}
		if (Input.GetKeyDown(KeyCode.Alpha1))
		{
			m_Camera.transform.position = new Vector3(0, 166, m_Camera.transform.position.z);
		}
		if (Input.GetKeyDown(KeyCode.Alpha2))
		{
			m_Camera.transform.position = new Vector3(452.5f, 100, m_Camera.transform.position.z);
		}
		if (Input.GetKeyDown(KeyCode.Alpha3))
		{
			m_Camera.transform.position = new Vector3(452.5f, 166, m_Camera.transform.position.z);
		}
	}

	////////////////////////////////////////////////////////////////

	public override void OnLateTick()
	{
        if (!m_DoCameraLogic)
        {
            return;
        }

		// Debug: Tick only every x ticks!
		if (GameCore.Get().GetCurrentTickCalls() % m_TickEvery != 0)
		{
			return;
		}
		

		PlayerController player = PlayerManager.Get().GetPlayerController();

		if (m_Camera == null)
		{
			m_Camera        = Camera.main;
			m_CameraHolder  = m_Camera?.transform.parent.gameObject;
		}

		if (player == null || m_Camera == null || m_CameraHolder == null)
		{
			return;
		}

		////////////////////////////////////////////////////////////////
		
		// 1) Tick Camera mode
		TickCameraModes(player);

		////////////////////////////////////////////////////////////////
		
		// 2) Tick Move
		TickCameraMovement();

		////////////////////////////////////////////////////////////////
		
		// 3) Tick Scale
		TickCameraScale();

		////////////////////////////////////////////////////////////////
		
		// 4 ) Tick Trauma
		TickTrauma();
	}

	////////////////////////////////////////////////////////////////
	
	public void FocusCameraOn(Vector2 position)
	{
		if (m_CameraHolder == null)
		{
			Debug.LogError("No camera");
			return;
		}

		m_FocusPosition                     = position;
		m_CameraHolder.transform.position   = new Vector3(position.x, position.y, m_CameraHolder.transform.position.z);
	}
	
	////////////////////////////////////////////////////////////////
	
	public void OnPlayerEnteredAnchorZone(CameraAnchorZone anchorZone)
	{
		//Debug.Log("Enter Anchor Zone");

		m_CurrentAnchorZone?.SetCameraCollidersActive(false);
		m_CurrentAnchorZone = anchorZone;
		SetCameraMode(CameraModes.TransitionToAnchorZone);

		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		m_PreviousScaleFactor           = m_ZoomFactor;
		m_DesiredScaleFactor            = anchorZone.CameraZoom;
		m_ZoomTimeRemaining             = assetDataCamera.ZoomTime;
		anchorZone.SetCameraCollidersActive(true);
	}

	////////////////////////////////////////////////////////////////
	
	public void OnPlayerLeftAnchorZone(CameraAnchorZone anchorZone)
	{
		//Debug.Log("Exit Anchor Zone");
		
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		m_PreviousScaleFactor           = m_ZoomFactor;
		m_DesiredScaleFactor            = 1.0f;
		m_ZoomTimeRemaining             = assetDataCamera.ZoomTime;

		if (m_CurrentAnchorZone != anchorZone)
		{
			// Valid Case: If two zones are close by, it can happen that we first enter the new zone, then leave the old zone. Unity Events I guess.
			return;
		}

		////////////////////////////////////////////////////////////////

		m_CurrentAnchorZone?.SetCameraCollidersActive(false);

		m_CurrentAnchorZone = null;

		if (m_CameraMode != CameraModes.LockOnPlayer)
		{
			SetCameraMode(CameraModes.TransitionToFocusPlayer);
		}
	}

	////////////////////////////////////////////////////////////////

	void SetCameraMode(CameraModes mode)
	{
		m_CameraMode = mode;
		//PhysicsManager.Get().SetPhysicsSimulationPaused(mode == CameraModes.TransitionToAnchorZone);
	}
	
	////////////////////////////////////////////////////////////////

	bool ContainsCameraAnchorCollider(Collider2D[] colliders, int checkLength)
	{
		for (int i = 0; i < checkLength; i++)
		{
			if (!colliders[i].gameObject.GetComponent<CameraAnchorZone>())
			{
				continue;
			}

			return true;

		}
		return false;
	}

	////////////////////////////////////////////////////////////////

	int? GetFirstNonTriggerCollider(RaycastHit2D[] raycastHits, int checkLength, bool ignoreAnchorCollider)
	{
		for (int i = 0; i < checkLength; i++)
		{
			// Ignore triggers (how ever we found them
			if (raycastHits[i].collider.isTrigger)
			{
				continue;
			}

			if (raycastHits[i].collider.gameObject.GetComponent<CameraAnchorZone>())
			{
				if (ignoreAnchorCollider)
				{
					continue;
				}
			}

			////////////////////////////////////////////////////////////////

			return i;

		}
		return null;
	}

	public Vector2 GetCameraColliderSize()
	{ 
		return new Vector2(m_BaseSizeX * 2.0f / m_ZoomFactor, m_BaseSizeY * 2.0f / m_ZoomFactor);
	}

	////////////////////////////////////////////////////////////////
	
	public static Vector2 GetCameraColliderSize_StaticValue()
	{
		return new Vector2(21.33754f, 12.0f);
	}

	////////////////////////////////////////////////////////////////
	
	void TickCameraModes(PlayerController player)
	{
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();

		// 1) Find Target Position
		Vector2 cameraColliderSize      = GetCameraColliderSize();
		Vector3 desiredFocusPosition    = Vector3.zero;
		switch (m_CameraMode)
		{
			case CameraModes.FocusPlayer:
			case CameraModes.TransitionToFocusPlayer:
			case CameraModes.LockOnPlayer:
				Vector2 movementPrediction  = player.SmoothMovingDirection * assetDataCamera.MovementDirectionOffsetPercentages * cameraColliderSize;
				desiredFocusPosition        = player.transform.position + movementPrediction.To3D_Z();
				break;
			case CameraModes.TransitionToAnchorZone:
				desiredFocusPosition = m_CurrentAnchorZone.GetTargetCameraPosition(cameraColliderSize, PlayerManager.Get().GetPlayerController().transform.position);
				break;
		}
		
		////////////////////////////////////////////////////////////////
		
		// 2) Find Reachable Target Position
		
		float outSideThreshholdFactorX = 1.0f;
		float outSideThreshholdFactorY = 1.0f;

		Vector2 reachableDesiredFocusPositionWS = desiredFocusPosition;
		{
			Vector2 desiredFocusDelta   = desiredFocusPosition.xy() - m_CameraHolder.transform.position.xy();
			Vector2 cameraExtendsWS     = m_Camera.orthographicSize * new Vector2(1.0f, 1.0f * 9.0f / 16.0f);
			Vector2 cameraThreshholdWS  = cameraExtendsWS * new Vector2(1.0f - assetDataCamera.MoveThreshholdPercentageX, 1.0f - assetDataCamera.MoveThreshholdPercentageY);
			
			if (m_DoDebugRendering)
			{
				DebugDrawingInterface.DrawPersistentPointBox(m_CameraHolder.transform.position.xy(), 0.2f, Color.gray, m_TickEvery - 1);
				DebugDrawingInterface.DrawPersistentPointBox(m_FocusPosition, 0.2f, Color.white, m_TickEvery - 1);
				DebugDrawingInterface.DrawPersistentPointBox(reachableDesiredFocusPositionWS, 0.2f, Color.black, m_TickEvery - 1);
				Vector2 cameraThreshholdBottomRight = m_CameraHolder.transform.position.xy() - cameraThreshholdWS;
				DebugDrawingInterface.DrawPersistentLines(new List<Vector3>() {
				cameraThreshholdBottomRight, 
				cameraThreshholdBottomRight + new Vector2(2 * cameraThreshholdWS.x, 0), 
				cameraThreshholdBottomRight + new Vector2(2 * cameraThreshholdWS.x, 0), 
				cameraThreshholdBottomRight + 2 * cameraThreshholdWS, 
				cameraThreshholdBottomRight + 2 * cameraThreshholdWS, 
				cameraThreshholdBottomRight + new Vector2(0, 2 * cameraThreshholdWS.y),
				cameraThreshholdBottomRight + new Vector2(0, 2 * cameraThreshholdWS.y),
				cameraThreshholdBottomRight
				}, Color.white, m_TickEvery - 1);
				DebugDrawingInterface.DrawPersistentSSText("Anchor Zone: " + m_CurrentAnchorZone?.ToString(), new Vector2(5, 85), Color.white, m_TickEvery - 1);
			}

			if (m_CameraMode == CameraModes.FocusPlayer || m_CameraMode == CameraModes.TransitionToFocusPlayer)
			{
				// Modify Target Position if we are focusing the player.
				float absFocusDeltaX                        = Mathf.Abs(desiredFocusDelta.x);
				float absFocusDeltaY                        = Mathf.Abs(desiredFocusDelta.y);
				bool isPlayerOutsideOfCameraThreshholdX     = absFocusDeltaX > cameraThreshholdWS.x;
				bool isPlayerOutsideOfCameraThreshholdY     = absFocusDeltaY > cameraThreshholdWS.y;
				
				outSideThreshholdFactorX = MathExtensions.Remap(absFocusDeltaX, cameraThreshholdWS.x, cameraExtendsWS.x, 1.0f, 1.5f);
				outSideThreshholdFactorY = MathExtensions.Remap(absFocusDeltaY, cameraThreshholdWS.y, cameraExtendsWS.y, 1.0f, 1.5f);

				////////////////////////////////////////////////////////////////
		
				reachableDesiredFocusPositionWS = m_CameraHolder.transform.position;
				if (isPlayerOutsideOfCameraThreshholdX)
				{
					if (desiredFocusDelta.x < 0)
					{
						reachableDesiredFocusPositionWS.x -= (-cameraThreshholdWS.x - desiredFocusDelta.x); 
					}
					else
					{
						reachableDesiredFocusPositionWS.x -= (cameraThreshholdWS.x - desiredFocusDelta.x); 
					}
				}
			
				if (isPlayerOutsideOfCameraThreshholdY)
				{
					if (desiredFocusDelta.y < 0)
					{
						reachableDesiredFocusPositionWS.y -= (-cameraThreshholdWS.y - desiredFocusDelta.y); 
					}
					else
					{
						reachableDesiredFocusPositionWS.y -= (cameraThreshholdWS.y - desiredFocusDelta.y); 
					}
				}
			}

		}

		////////////////////////////////////////////////////////////////
		
		// 3) Find focus position
		
		if (m_CameraMode != CameraModes.LockOnPlayer)
		{
			m_FocusPosition   = new Vector2(Mathf.Lerp(m_FocusPosition.x, reachableDesiredFocusPositionWS.x, assetDataCamera.Smoothness * Time.deltaTime * outSideThreshholdFactorX), 
											Mathf.Lerp(m_FocusPosition.y, reachableDesiredFocusPositionWS.y, assetDataCamera.Smoothness * Time.deltaTime * outSideThreshholdFactorY));

		}
		else
		{
			m_FocusPosition = reachableDesiredFocusPositionWS;
		}

		Vector2 focusDelta          = m_FocusPosition - m_CameraHolder.transform.position.xy();
		
		////////////////////////////////////////////////////////////////
		
		// 4) Check for reached target
		bool reachedTarget = (Mathf.Abs(focusDelta.x) < 0.1f) && (Mathf.Abs(focusDelta.y) <  0.1f);
		if (reachedTarget)
		{
			switch (m_CameraMode)
			{
				case CameraModes.LockOnPlayer:
				case CameraModes.FocusPlayer:
					break;

				case CameraModes.TransitionToFocusPlayer:
					SetCameraMode(CameraModes.FocusPlayer);
					break;
				case CameraModes.TransitionToAnchorZone:
					SetCameraMode(CameraModes.FocusPlayer);
					break;
			}
		}
	}


	////////////////////////////////////////////////////////////////

	void TickCameraMovement()
	{
		float SIGNIFICANT_MOVE      = 0.01f;
		
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		
		////////////////////////////////////////////////////////////////
		
		Vector2 unclampedBaseMoveVector     = m_FocusPosition - m_CameraHolder.transform.position.xy();
		float unclampedBaseMoveMagnitude    = unclampedBaseMoveVector.magnitude;
		
		// Only for non locked modes
		float movementSimilarity            = Vector2.Dot(m_LastLerpedCameraMove, (m_CameraHolder.transform.position.xy() - m_FocusPosition).normalized);
		float movementSimilarityFactor      = MathExtensions.Remap(movementSimilarity, -1.0f, 1.0f, 0.1f, 1.0f);

		if (m_CameraMode != CameraModes.LockOnPlayer)
		{
			float lerpFactor                    = assetDataCamera.Smoothness2 * Time.deltaTime;

			unclampedBaseMoveVector             = m_FocusPosition - Vector2.Lerp(m_CameraHolder.transform.position.xy(), m_FocusPosition, assetDataCamera.Smoothness2 * Time.deltaTime * movementSimilarityFactor); // m_FocusPosition - m_Camera.transform.position.xy();
			unclampedBaseMoveMagnitude          = unclampedBaseMoveVector.magnitude;
		}

		m_CollidedXLastTick             = false;
		m_CollidedYLastTick             = false;
		
		if (m_DoDebugRendering)
		{
			DebugDrawingInterface.DrawPersistentLine(m_CameraHolder.transform.position.xy(), m_CameraHolder.transform.position.xy() + unclampedBaseMoveVector, new Color(0.0f, 0.0f, 1.0f, 0.8f), m_TickEvery - 1);
			DebugDrawingInterface.DrawPersistentLine(m_FocusPosition, m_CameraHolder.transform.position.xy(), new Color(1.0f, 1.0f, 1.0f, 0.3f), m_TickEvery - 1);
			DebugDrawingInterface.DrawPersistentLine(m_CameraHolder.transform.position.xy(), m_CameraHolder.transform.position.xy() + m_LastLerpedCameraMove, new Color(1.0f, 0.0f, 0.0f, 0.8f), m_TickEvery - 1);
			DebugDrawingInterface.DrawPersistentSSText("Uncl Lerped. Delta: " + unclampedBaseMoveVector.x + ", Y " + unclampedBaseMoveVector.y, new Vector2(5, 105), Color.white, m_TickEvery - 1);
			DebugDrawingInterface.DrawPersistentSSText("Camera Mode: " + m_CameraMode.ToString() , new Vector2(5, 135), Color.white, m_TickEvery - 1);
			DebugDrawingInterface.DrawPersistentSSText("CollidedLastTick X: " + m_CollidedXLastTick.ToString() + ", Y: " + m_CollidedYLastTick.ToString(), new Vector2(5, 165), Color.white, m_TickEvery - 1);
		}

		if (Mathf.Abs(unclampedBaseMoveVector.x) < SIGNIFICANT_MOVE && Mathf.Abs(unclampedBaseMoveVector.y) < SIGNIFICANT_MOVE)
		{
			return;
		}
		
		//Vector2 clampedDelta            = new Vector2(Mathf.Clamp(deltaX, -assetDataCamera.MaximumSpeedFreeCam, assetDataCamera.MaximumSpeedFreeCam), 
		//								                Mathf.Clamp(deltaY, -assetDataCamera.MaximumSpeedFreeCam, assetDataCamera.MaximumSpeedFreeCam));

		// Get colliders we collide with
		//Vector2 cameraCollider      = GetCameraColliderSize();
		//float checkDistance         = assetDataCamera.MaximumSpeedFreeCam * Mathf.Sqrt(2);
		//m_RaycastHitCount           = Physics2D.BoxCastNonAlloc(m_Camera.transform.position, cameraCollider, 0.0f, unclampedMoveVector, m_RaycastHits, checkDistance, (int) CommonLayerMasks.Camera);
		//int? colliderIndex          = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, ignoreAnchorCollider);
		
		//// Early Out: We do not collide with anything, so we can just move
		//if (!colliderIndex.HasValue)
		//{
		//	Vector2 moveVector  = unclampedMoveVector;
		//	moveVector.x        = Mathf.Clamp(moveVector.x, -assetDataCamera.MaximumSpeedFreeCam, assetDataCamera.MaximumSpeedFreeCam);
		//	moveVector.y        = Mathf.Clamp(moveVector.y, -assetDataCamera.MaximumSpeedFreeCam, assetDataCamera.MaximumSpeedFreeCam);

		//	m_Camera.transform.position += moveVector.To3D_Z();
		//	return;
		//}

		////////////////////////////////////////////////////////////////
		// 2) Perform movement along these axes
		
		bool ignoreAnchorColliders  = m_CameraMode == CameraModes.TransitionToAnchorZone || m_CameraMode == CameraModes.TransitionToFocusPlayer || m_CameraMode == CameraModes.LockOnPlayer;

		Vector2 moveAxisMain        = Vector2.right;
		Vector2 moveAxisSecondary   = Vector2.up;
		Vector2 moveMain            = moveAxisMain      * Vector2.Dot(unclampedBaseMoveVector, moveAxisMain);
		Vector2 moveSecondary       = moveAxisSecondary * Vector2.Dot(unclampedBaseMoveVector, moveAxisSecondary);

		Vector2 newPosition         = m_CameraHolder.transform.position.xy();

		// Movement Main (X only at the moment)
		bool performMovementMain = Mathf.Abs(moveMain.x) > SIGNIFICANT_MOVE;
		if (performMovementMain)
		{
			Vector2 validPositionAfterMove  = newPosition + moveMain;

			// Move the full move ?
			float paddingX = m_BaseSizeX * 1.0f / m_ZoomFactor;
			m_RaycastHitCount   = Physics2D.RaycastNonAlloc(newPosition, moveMain, m_RaycastHits, paddingX + moveMain.magnitude, (int) CommonLayerMasks.Camera);
			int? colliderIndex  = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, ignoreAnchorColliders);
			
			if (colliderIndex.HasValue)
			{
				Debug.Assert(!m_RaycastHits[colliderIndex.Value].collider.isTrigger);

				////////////////////////////////////////////////////////////////

				// No, we hit something: Only move until the collision!
				validPositionAfterMove.x = m_RaycastHits[colliderIndex.Value].point.x - (moveMain.normalized.x * paddingX); 
				
				m_CollidedXLastTick = true;
			}
	   
			newPosition = validPositionAfterMove;
		}
		
		// Movement Secondary (Y only at the moment)
		bool performMovementSecondary = Mathf.Abs(moveSecondary.y) > SIGNIFICANT_MOVE;
		if (performMovementSecondary)
		{
			Vector2 validPositionAfterMove  = newPosition + moveSecondary;

			// Move the full move ?
			float paddingY = m_BaseSizeY * 1.0f / m_ZoomFactor;
			m_RaycastHitCount   = Physics2D.RaycastNonAlloc(newPosition, moveSecondary, m_RaycastHits, paddingY + moveSecondary.magnitude, (int) CommonLayerMasks.Camera);
			int? colliderIndex  = GetFirstNonTriggerCollider(m_RaycastHits, m_RaycastHitCount, ignoreAnchorColliders);
			
			if (colliderIndex.HasValue)
			{
				Debug.Assert(!m_RaycastHits[colliderIndex.Value].collider.isTrigger);

				////////////////////////////////////////////////////////////////

				// No, we hit something: Only move until the collision!
				validPositionAfterMove.y = m_RaycastHits[colliderIndex.Value].point.y - (moveSecondary.normalized.y *paddingY); 

				m_CollidedYLastTick = true;
			}
	   
			newPosition = validPositionAfterMove;
		}
		
		float startSlowMoveAtDistance   = 2.0f;
		float minMoveFactor             = 0.1f;
		float maxMove                   = Mathf.Lerp(assetDataCamera.MaximumSpeedFreeCam * minMoveFactor, assetDataCamera.MaximumSpeedFreeCam, unclampedBaseMoveMagnitude / startSlowMoveAtDistance);
		
		if (m_CameraMode == CameraModes.LockOnPlayer)
		{
			maxMove *= 2.5f;
		}

		//DebugDrawingInterface.DrawPersistentSSText("MaxMove " + maxMove.ToString() + " (MoveMagnitude: " + unclampedBaseMoveMagnitude + ")", new Vector2(5, 195), Color.blue, m_TickEvery);

		Vector2 moveVector          = newPosition - m_CameraHolder.transform.position.xy();
		moveVector.x                = Mathf.Clamp(moveVector.x, -maxMove, maxMove);
		moveVector.y                = Mathf.Clamp(moveVector.y, -maxMove, maxMove);
		
		if (m_CameraMode != CameraModes.LockOnPlayer)
		{
			m_LastLerpedCameraMove      = Vector2.Lerp(m_LastLerpedCameraMove.normalized, moveVector.normalized, movementSimilarityFactor);
		}
		else
		{
			m_LastLerpedCameraMove      = moveVector.normalized;
		}

		m_CameraHolder.transform.position += moveVector.To3D_Z();
	}

	#region shake

	public void AddTrauma(float amount)
	{
		m_CurrentTrauma += amount;
	}

	////////////////////////////////////////////////////////////////

	public void ResetTrauma()
	{
		m_CurrentTrauma = 0.0f;
		m_CurrentSmoothedTrauma = 0.0f;
	}

	////////////////////////////////////////////////////////////////
	
	public void TickTrauma()
	{
		if (m_CurrentTrauma == 0.0f)
		{
			return;
		}

		////////////////////////////////////////////////////////////////

		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		m_CurrentTrauma -= assetDataCamera.ReduceTraumaPerTickBy;

		if (m_CurrentTrauma <= 0.0f)
		{
			// End Trauma!
			ResetTrauma();
			m_Camera.transform.localPosition = Vector3.zero;
			return;
		}

		m_CurrentSmoothedTrauma             = Mathf.Lerp(m_CurrentSmoothedTrauma, m_CurrentTrauma, 0.2f);

		float currentTraumaSQ               = m_CurrentSmoothedTrauma * m_CurrentSmoothedTrauma;
		
		float noise1                        = Mathf.PerlinNoise(Time.time * assetDataCamera.ScreenShakeSpeed, 0.0f) * assetDataCamera.ScreenShakeMaxAmountX - assetDataCamera.ScreenShakeMaxAmountX / 2.0f;
		float noise2                        = Mathf.PerlinNoise(0.0f, Time.time * assetDataCamera.ScreenShakeSpeed) * assetDataCamera.ScreenShakeMaxAmountY - assetDataCamera.ScreenShakeMaxAmountY / 2.0f;
		
		Vector2 screenShakePositionOffset   = new Vector2(currentTraumaSQ * noise1, currentTraumaSQ * noise2);
		m_Camera.transform.localPosition    = screenShakePositionOffset.To3D_Z();
	}

	#endregion
	

	////////////////////////////////////////////////////////////////
	
	public float ZoomTo(float targetZoom, float time)
	{
		m_PreviousScaleFactor           = m_ZoomFactor;
		m_DesiredScaleFactor            = targetZoom;
		m_ZoomTimeRemaining             = time;

		return m_PreviousScaleFactor;
	}
}
