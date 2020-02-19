using JSDK.Events;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering.LWRP;
using UnityEngine.InputSystem;

[System.Serializable]
/*
 * Ticks the player as well as their progress in terms of spawn points
 * Also manages platforms and blobs
 */
public class PlayerManager : IManager
{	
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static PlayerManager Get()
	{
		return GameCore.Get().GetManager<PlayerManager>();
	}
	
	[SerializeField] private PlayerController       m_PlayerController          = null;  
	[SerializeField] private int                    m_Debug_UpdatePlayerEvery   = 1;
	
	public enum GameState
	{
		Pregameplay = 0,
		Cutscene    = 1,
		Gameplay    = 2
	}
	
	public GameState _GameState { get { return m_GameState; } }
	[SerializeField] private GameState m_GameState = GameState.Gameplay;

	[ExposeToLua]
	public static void SetGameState(int value)
	{
		Get().m_GameState = (GameState) value;

	}

	// Spawn Points
	[SerializeField] private List<PlayerSpawnPoint> m_PlayerSpawnPoints         = new List<PlayerSpawnPoint>();
	[SerializeField] private PlayerSpawnPoint       m_CurrentPlayerSpawnPoint   = null; 
	[SerializeField] private ParticleSystem         m_CurrentSaveParticleSystem = null;
	
	[ExposeToLua]
	public static void SetRegainDashOnGroundContact(bool value)
	{
		PlayerManager.Get().RegainDashOnGroundContact = value;

		if (value == false)
		{
			PlayerManager.Get().GetPlayerController().DashAvailable = false;
			PlayerManager.Get().GetPlayerController().SetOthColorStateImmediate(PlayerController.OthColorState.Disabled);
		}

	}

	[ExposeToLua]
	public static void UpdateOthSprite()
	{
		if (!PlayerManager.Get().RegainDashOnGroundContact)
		{
			PlayerManager.Get().GetPlayerController().SetOthColorStateImmediate(PlayerController.OthColorState.Disabled);
		}
		else
		{
			PlayerManager.Get().GetPlayerController().SetOthColorStateImmediate(PlayerController.OthColorState.Charged);
		}
	}

	[ExposeToLua]
	public static void StartFinalSequence()
	{
		FinalCutSceneHandler finalCutSceneHelper = GameObject.FindObjectOfType<FinalCutSceneHandler>();
		if (finalCutSceneHelper == null)
		{
			Debug.LogError("Could not find FinalCutSceneHandler");
			return;
		}

		finalCutSceneHelper.StartFinalSequence();
	}
	
	private bool m_RegainDashOnGroundContact = false;
	public bool RegainDashOnGroundContact {
		get { return m_RegainDashOnGroundContact; }
		set { m_RegainDashOnGroundContact = value; }
	}

	public bool AcceptPlayerInput { get { return m_AcceptPlayerInput; } set { m_AcceptPlayerInput = value; } }
	[SerializeField] private bool m_AcceptPlayerInput = true;

	private bool m_DrawCurrentSpawnID = false;

	[ExposeToLua]
	public static void SetDrawCurrentSpawnID(bool value)
	{
		Get().m_DrawCurrentSpawnID = value;
	}

	////////////////////////////////////////////////////////////////

	[ExposeToLua]
	public static void SetSpawnID(int value)
	{
		for (int i = 0; i < Get().m_PlayerSpawnPoints.Count; i++)
		{
			if (Get().m_PlayerSpawnPoints[i] == null)
			{
				// This happens when the scene of the spawn point is unloading but has not yet fired the "unloading done" event.
				// maybe there is a nicer way to handle this?
				continue;
			}

			if (Get().m_PlayerSpawnPoints[i].Index == value)
			{
				Get().SetPlayerSpawnPoint(Get().m_PlayerSpawnPoints[i]);
				break;
			}
		}

		Get().Reset();
	}

	////////////////////////////////////////////////////////////////
	
	[ExposeToLua]
	public static void ResetLevel()
	{
		SetSpawnID(0);
	}

	////////////////////////////////////////////////////////////////

	// Platforms
	struct Platform
	{
		public PlatformData    _Data;
		public GameObject      _GameObject;
	}
	
	[SerializeField] private GameObject m_PlatformCoreObject;
	[SerializeField] private List<Platform> m_Platforms = new List<Platform>();

	// Blobs
	struct BlobHiveData
	{
		public BlobHive     _Hive;
		public GameTicks[]  _PloppedTimers; // 0 = Not Plopped. Anything above: Ticks until unplop happens.
		public bool         _Active;
	}
	[SerializeField] private List<BlobHiveData> m_BlobHives = new List<BlobHiveData>();


	// Reset Plants
	class ResetPlantData
	{
		public static GameTicks     NOT_PLOPPED = new GameTicks(-1);
		public ResetPlantComponent  _ResetPlant;
		public GameTicks            _PloppedTimer;
		public bool                 _Respawns;
	}
	[SerializeField] private List<ResetPlantData> m_ResetPlantData          = new List<ResetPlantData>();

	[SerializeField] private List<ResetPlantSpawner> m_ResetPlantSpawners   = new List<ResetPlantSpawner>();
	
	[SerializeField] private TotemComponent m_Totem = null;

	// Reset
	private bool m_IsResetting = false;

	////////////////////////////////////////////////////////////////

	[ExposeToLua]
	public static void SetUpdatePlayerEvery(int value)
	{
		Get().m_Debug_UpdatePlayerEvery = value;
	}

	public int GetDebugDrawTickCountForPlayer()
	{
		return m_Debug_UpdatePlayerEvery - 1;
	}

	////////////////////////////////////////////////////////////////

	public struct PlayerSpawnPointData
	{
		public readonly LevelID Level;
		public readonly int     ID;

		public PlayerSpawnPointData(LevelID level, int id)
		{
			Level   = level;
			ID      = id;
		}
	}
	// Only for loading & game start
	[SerializeField] private PlayerSpawnPointData? m_PlayerSpawnPointToStartFrom = null;
	
	////////////////////////////////////////////////////////////////

	public override void Initialize()
	{
		JSDK.Events.EventManager.Get().AddListener<PlayerDeathEvent>(OnPlayerDeathEvent);
		JSDK.Events.EventManager.Get().AddListener<LevelLoadedEvent>(OnLevelLoadedEvent);
		JSDK.Events.EventManager.Get().AddListener<LevelUnLoadedEvent>(OnLevelUnloadedEvent);
		
		GameCore.Get().GetInput().Player.Reset.performed += HandlePlayerReset;
		GameCore.Get().GetInput().Player.Reset.Enable();

		CreatePlatformCoreObject();
		CreateSpawnPointEffectObject();
	}

	////////////////////////////////////////////////////////////////

	public override void Uninitialize()
	{
		DestroyPlatformCoreObject();

		GameCore.Get().GetInput().Player.Reset.performed -= HandlePlayerReset;
		GameCore.Get().GetInput().Player.Reset.Disable();
		
		JSDK.Events.EventManager.Get().RemoveListener<LevelUnLoadedEvent>(OnLevelUnloadedEvent);
		JSDK.Events.EventManager.Get().RemoveListener<LevelLoadedEvent>(OnLevelLoadedEvent);
		JSDK.Events.EventManager.Get().RemoveListener<PlayerDeathEvent>(OnPlayerDeathEvent);

	}

	////////////////////////////////////////////////////////////////

	public override void Serialize(Weinschmecker.Serializer serializer)
	{
		if (serializer.IsLoading())
		{
			int currentSpawnPointIndex          = -1;
			LevelID currentSpawnPointLevel      = LevelID.Invalid;
			serializer.Serialize("CurrentSpawnPointIndex", ref currentSpawnPointIndex, -1);
			serializer.Serialize("CurrentSpawnPointLevel", ref currentSpawnPointLevel, LevelID.Invalid);
			SetDesiredPlayerSpawnPoint(currentSpawnPointLevel, currentSpawnPointIndex);
		}
		else
		{
			if (m_CurrentPlayerSpawnPoint != null)
			{
				int currentSpawnPointIndex      = m_CurrentPlayerSpawnPoint.Index;
				LevelID currentSpawnPointLevel  = m_CurrentPlayerSpawnPoint._LevelID;

				serializer.Serialize("CurrentSpawnPointIndex", ref currentSpawnPointIndex, -1);
				serializer.Serialize("CurrentSpawnPointLevel", ref currentSpawnPointLevel, LevelID.Invalid);
			}
		}

		serializer.Serialize("RegainDashOnGroundContact", ref m_RegainDashOnGroundContact, false);
	}

	////////////////////////////////////////////////////////////////

	public override void Tick()
	{
		if (m_PlayerController == null)
		{
			// Happens when no level is loaded
			return;
		}

		////////////////////////////////////////////////////////////////
		
		if (m_IsResetting)
		{
			return;
		}

		////////////////////////////////////////////////////////////////
		
		if (GameCore.Get().GetManager<PhysicsManager>().IsPhysicsSimulationPaused())
		{
			return;
		}

		////////////////////////////////////////////////////////////////

		// Debug: Tick only every x ticks!
		if (GameCore.Get().GetCurrentTickCalls() % m_Debug_UpdatePlayerEvery != 0)
		{
			return;
		}

		////////////////////////////////////////////////////////////////

		PlokothInput input = GameCore.Get().GetInput();
		if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.JoystickButton6))
		{
			StatisticManager.Get().ToggleDrawStats();
		}

		////////////////////////////////////////////////////////////////
		// Shall we start the game?

		if (m_GameState == GameState.Pregameplay)
		{
			bool startGameplay = Input.anyKeyDown || input.Player.Jump.ReadValue<float>() != 0.0f || input.Player.Dash.ReadValue<float>() != 0.0f || 
								 input.Player.StickDirection.ReadValue<Vector2>().x != 0.0f || input.Player.StickDirection.ReadValue<Vector2>().y != 0.0f;

			if (!startGameplay)
			{
				return;
			}

			m_GameState = GameState.Cutscene;

			////////////////////////////////////////////////////////////////
			
			LogoFadeHandler introLogo = GameObject.FindObjectOfType<LogoFadeHandler>();
			if (!introLogo)
			{
				Debug.LogError("Could not find introLogo object");
				return;
			}

			introLogo.StartLogoFade();
		}

		if (m_GameState == GameState.Cutscene)
		{
			return;
		}

		//////////////////////////////////////////////////////////////// 

		m_PlayerController.Tick();
		TickPlatforms();
		TickSpawnPoints();
		TickBlobs();
		TickResetPlants();

		if (m_CurrentPlayerSpawnPoint != null && m_DrawCurrentSpawnID)
		{
			DebugDrawingInterface.DrawPersistentSSText("SavePoint " + m_CurrentPlayerSpawnPoint._LevelID.ToString() + "_" + m_CurrentPlayerSpawnPoint.Index, new Vector2(Screen.width - 200, 5), Color.white, m_Debug_UpdatePlayerEvery);
		}
	}

	////////////////////////////////////////////////////////////////

	private void OnLevelLoadedEvent(LevelLoadedEvent e)
	{
		SceneContext gameSceneContext = SceneContext.LevelContext(e.Level);

		////////////////////////////////////////////////////////////////
		// Find spawn points

		List<PlayerSpawnPoint> playerSpawnPoints = new List<PlayerSpawnPoint> (Object.FindObjectsOfType<PlayerSpawnPoint>());            
		playerSpawnPoints.RemoveAll((PlayerSpawnPoint psp) => {
			return psp._LevelID != e.Level;
		});
		playerSpawnPoints.Sort((PlayerSpawnPoint lhs, PlayerSpawnPoint rhs) => {
			if (lhs.Index == rhs.Index) 
			{
				return 0;
			}

			return (lhs.Index < rhs.Index) ? -1 : 1;
		});
		bool success = playerSpawnPoints.Count != 0;
		Debug.Assert(success, "Could not find a player spawn point for scene " + e.Level.ToString());

		m_PlayerSpawnPoints.AddRange(playerSpawnPoints);

		////////////////////////////////////////////////////////////////
		// Find blob hives

		List<BlobHive> blobHives = new List<BlobHive> (Object.FindObjectsOfType<BlobHive>());          
		blobHives.RemoveAll((BlobHive bh) => {
			return bh._LevelID != e.Level;
		});
		for (int i = 0; i < blobHives.Count; i++)
		{
			m_BlobHives.Add(new BlobHiveData{_Hive = blobHives[i], _Active = true, _PloppedTimers = new GameTicks[blobHives[i].Blobs.Count]});
		}

		////////////////////////////////////////////////////////////////
		// Find reset plants

		List<ResetPlantComponent> resetPlants = new List<ResetPlantComponent> (Object.FindObjectsOfType<ResetPlantComponent>());      
		resetPlants.RemoveAll((ResetPlantComponent rpc) => {
			return rpc._LevelID != e.Level;
		});
		for (int i = 0; i < resetPlants.Count; i++)
		{
			m_ResetPlantData.Add(new ResetPlantData{_ResetPlant = resetPlants[i], _PloppedTimer = new GameTicks(), _Respawns = true});
		}

		////////////////////////////////////////////////////////////////
		// Find reset plant spawners

		List<ResetPlantSpawner> resetPlantSpawners = new List<ResetPlantSpawner> (Object.FindObjectsOfType<ResetPlantSpawner>());
		resetPlantSpawners.RemoveAll((ResetPlantSpawner rps) => {
			return rps._LevelID != e.Level;
		});
		m_ResetPlantSpawners.AddRange(resetPlantSpawners);

		////////////////////////////////////////////////////////////////
		// Find totem

		m_Totem = Object.FindObjectOfType<TotemComponent>();

		////////////////////////////////////////////////////////////////
		
		gameSceneContext.Unset();

		////////////////////////////////////////////////////////////////
		
		if (e.SpawnPlayerAt.HasValue)
		{
			SetDesiredPlayerSpawnPoint(e.SpawnPlayerAt.Value.Level, e.SpawnPlayerAt.Value.ID);
		}

		bool findSpawnPoint = e.LoadedDueToSerialize || (e.StartGameFromLevel && m_PlayerSpawnPointToStartFrom.HasValue && m_PlayerSpawnPointToStartFrom.Value.Level == e.Level);
		if (findSpawnPoint)
		{
			InitializePlayerSpawnPointToStartFrom(e.Level);
		}

		////////////////////////////////////////////////////////////////

		bool spawnPlayer = e.StartGameFromLevel || (e.LoadedDueToSerialize && m_PlayerSpawnPointToStartFrom.HasValue && m_PlayerSpawnPointToStartFrom.Value.Level == e.Level);
		if (spawnPlayer)
		{
			InitializePlayerForLevel(e.Level);
		}
	}

	////////////////////////////////////////////////////////////////
	
	private void OnLevelUnloadedEvent(LevelUnLoadedEvent e)
	{
		// Remove all spawn points from that level
		m_PlayerSpawnPoints.RemoveAll((PlayerSpawnPoint psp) => {
			return psp._LevelID == e.Level;
		});
				
		// Remove all blob hives from that level
		m_BlobHives.RemoveAll((BlobHiveData bhd) => {
			return bhd._Hive._LevelID == e.Level;
		});
		
		// Remove all reset plants from that level
		m_ResetPlantData.RemoveAll((ResetPlantData rpd) => {
			return rpd._ResetPlant._LevelID == e.Level;
		});

		// Remove all reset plant spawners from that level
		m_ResetPlantSpawners.RemoveAll((ResetPlantSpawner rps) => {
			return rps._LevelID == e.Level;
		});
		
		if (m_CurrentPlayerSpawnPoint != null)
		{
			if (m_CurrentPlayerSpawnPoint._LevelID == e.Level)
			{
				SetPlayerSpawnPoint(null);
			}
		}
	}

	////////////////////////////////////////////////////////////////

	private bool InitializePlayerSpawnPointToStartFrom(LevelID level)
	{
		SetPlayerSpawnPoint(m_PlayerSpawnPoints.Find((PlayerSpawnPoint psp) => {
			return psp.Index == m_PlayerSpawnPointToStartFrom.Value.ID && psp._LevelID == m_PlayerSpawnPointToStartFrom.Value.Level;
		}));
		
		SoundManager.Get().FitSoundToArea(m_CurrentPlayerSpawnPoint._AreaID);

		Debug.Assert(m_CurrentPlayerSpawnPoint != null, "Could not find serialized player spawnpoint ID " + m_PlayerSpawnPointToStartFrom.Value.ID + " in level " + level.ToString());

		return m_CurrentPlayerSpawnPoint != null;
	}

	////////////////////////////////////////////////////////////////

	private void InitializePlayerForLevel(LevelID level)
	{
		if (m_PlayerController == null || m_PlayerController.gameObject == null)
		{
			// Get player asset
			AssetDataPlayer assetData = AssetManager.GetAssetData<AssetDataPlayer>();

			// Spawn the player
			GameObject playerObject   = GameObject.Instantiate(assetData.PlayerPrefab, GameCore.Get().transform);
			m_PlayerController        = playerObject.GetComponentInChildren<PlayerController>();
		}

		AssetDataLevels assetDataLevels = AssetManager.GetAssetData<AssetDataLevels>();
		bool isIntroCutsceneLevel       = level == assetDataLevels.LevelWithIntroCutscene;
		m_GameState                     = isIntroCutsceneLevel ? GameState.Pregameplay : GameState.Gameplay;

		////////////////////////////////////////////////////////////////
		
		// 1st prio: current spawn point (set by loading level)
		if (m_CurrentPlayerSpawnPoint != null)
		{
			m_PlayerController.Reset(m_CurrentPlayerSpawnPoint.transform.position);
			return;
		}

		// 2nd prio: spawn at first spawnpoint in this level
		
		//Find all spawn points in level
		List<PlayerSpawnPoint> validSpawnPoints = m_PlayerSpawnPoints.FindAll((PlayerSpawnPoint psp) => {
			return psp._LevelID == level;
		});    

		bool success = validSpawnPoints.Count != 0;
		if (success)
		{
			// Sort by ID
			validSpawnPoints.Sort((PlayerSpawnPoint lhs, PlayerSpawnPoint rhs) => {
				if (lhs.Index == rhs.Index) 
				{
					return 0;
				}

				return (lhs.Index < rhs.Index) ? -1 : 1;
			});
			
			m_PlayerController.Reset(validSpawnPoints[0].transform.position);
		}
		else
		{
			m_PlayerController.Reset(Vector3.zero);
		}
		
		m_PlayerController.SetVisible(!isIntroCutsceneLevel);
	}

	////////////////////////////////////////////////////////////////
	
	public void SetDesiredPlayerSpawnPoint(LevelID level, int spawnPointID)
	{
		m_PlayerSpawnPointToStartFrom = new PlayerSpawnPointData(level, spawnPointID);
	}

	////////////////////////////////////////////////////////////////
	
	public PlayerController GetPlayerController()
	{
		return m_PlayerController;
	}
	
	////////////////////////////////////////////////////////////////
	
	void HandlePlayerReset(InputAction.CallbackContext context)
	{
		if (m_IsResetting)
		{
			return;
		}

		if (m_GameState != GameState.Gameplay)
		{
			return;
		}

		////////////////////////////////////////////////////////////////
		
		if (!GameCore.Get().AcceptKeyboardInput())
		{
			return;
		}
		
		////////////////////////////////////////////////////////////////
		
		if (!AcceptPlayerInput)
		{
			return;
		}

		////////////////////////////////////////////////////////////////

		bool value = false;
		if (context.valueType == typeof(bool))
		{
			value = context.ReadValue<bool>();
		}
		else if (context.valueType == typeof(float))
		{
			value = context.ReadValue<float>() > 0.1f;
		}

		if (value)
		{
			Reset();
		}
	}

	////////////////////////////////////////////////////////////////
	
	public void OnPlayerDeathEvent(PlayerDeathEvent e)
	{
		if (m_IsResetting)
		{
			return;
		}
		
		Reset();
	}

	////////////////////////////////////////////////////////////////
	
	void Reset()
	{
		GameCore.Get().StartCoroutine(C_Reset());
	}    

	////////////////////////////////////////////////////////////////

	IEnumerator C_Reset()
	{
		const float SHADER_ZERO_PERCENTAGE_VALUE    = 0.0f;
		const float SHADER_HALF_PERCENTAGE_VALUE    = 0.5f;
		const float SHADER_FULL_PERCENTAGE_VALUE    = 1.0f;

		////////////////////////////////////////////////////////////////

		if (m_IsResetting)
		{
			Debug.LogError("Already resetting");
			yield break;
		}

		////////////////////////////////////////////////////////////////

		AssetDataPlayer assetData   = AssetManager.GetAssetData<AssetDataPlayer>();
		m_IsResetting               = true;

		////////////////////////////////////////////////////////////////
		// 1) Start the death screen animation
		// ... Wait until the screen is completly covered by the effect.

		MeshRenderer effectRenderer         = CameraManager.Get().GetEffectRenderer();
		effectRenderer.material             = assetData.ScreenDeathTransitionMaterial;
		effectRenderer.enabled              = true;
		effectRenderer.transform.localScale = new Vector3(12f * Screen.width / Screen.height , 12f, 1f);

		string shaderPercentagePropertyName = "_Progress";
		int shaderPercentagePropertyID      = Shader.PropertyToID(shaderPercentagePropertyName);

		effectRenderer.material.SetFloat(shaderPercentagePropertyID, SHADER_ZERO_PERCENTAGE_VALUE);
		yield return new WaitForEndOfFrame();
	   
		float currentTime       = 0.0f;
		float effectTime        = assetData.ScreenDeathTransitionLength1stHalf;
		while (currentTime < effectTime)
		{
			currentTime         += Time.deltaTime;
			float percentage    = Mathf.SmoothStep(SHADER_ZERO_PERCENTAGE_VALUE, SHADER_HALF_PERCENTAGE_VALUE, currentTime / effectTime);
			percentage          = Mathf.Min(percentage, SHADER_HALF_PERCENTAGE_VALUE);

			effectRenderer.material.SetFloat(shaderPercentagePropertyID, percentage);

			yield return new WaitForEndOfFrame();
		}

		////////////////////////////////////////////////////////////////
		// 2) Execute the reset
		
		if (m_CurrentPlayerSpawnPoint != null)
		{
			m_PlayerController.Reset(m_CurrentPlayerSpawnPoint.transform.position);
		}
		else
		{
			if (m_PlayerSpawnPoints.Count != 0)
			{
				m_PlayerController.Reset(Vector3.zero);
			}
		}

		ResetPlatforms();
		ResetBlobs(m_CurrentPlayerSpawnPoint._LevelID);
		ResetResetPlants(m_CurrentPlayerSpawnPoint._LevelID);

		////////////////////////////////////////////////////////////////
		// 3) End the death screen animation
		// Wait until its done, then disable the renderer again.

		currentTime = 0.0f;
		effectTime  = assetData.ScreenDeathTransitionLength2ndHalf;
		while (currentTime < effectTime)
		{
			currentTime         += Time.deltaTime;
			float percentage    = Mathf.SmoothStep(SHADER_HALF_PERCENTAGE_VALUE, SHADER_FULL_PERCENTAGE_VALUE, currentTime / effectTime);
			percentage          = Mathf.Min(percentage, SHADER_FULL_PERCENTAGE_VALUE);

			effectRenderer.material.SetFloat(shaderPercentagePropertyID, percentage);
			
			yield return new WaitForEndOfFrame();
		}
				
		effectRenderer.material.SetFloat(shaderPercentagePropertyID, SHADER_FULL_PERCENTAGE_VALUE);
		yield return new WaitForEndOfFrame();

		effectRenderer.enabled      = false;
		m_IsResetting               = false;
	}

	////////////////////////////////////////////////////////////////
	
	void CreatePlatformCoreObject()
	{
		Debug.Assert(m_PlatformCoreObject == null);

		SceneContext sceneContext               = SceneContext.MetaContext();
		m_PlatformCoreObject                    = new GameObject("~ Platforms");
		m_PlatformCoreObject.transform.parent   = GameCore.Get().transform;

		sceneContext.Unset();
	}

	////////////////////////////////////////////////////////////////
	
	void DestroyPlatformCoreObject()
	{
		Debug.Assert(m_PlatformCoreObject != null);

		SceneContext sceneContext               = SceneContext.MetaContext();
		GameObject.Destroy(m_PlatformCoreObject);
		m_PlatformCoreObject = null;

		sceneContext.Unset();
	}

	////////////////////////////////////////////////////////////////
	
	public void AddPlatform(PlatformData platformData)
	{
		AssetDataPlayer assetDataPlayer = AssetManager.GetAssetData<AssetDataPlayer>();

		GameObject gameObject                   = new GameObject("~ Platform"); 
		gameObject.layer                        = (int) GameLayer.Platform;
		gameObject.transform.parent             = m_PlatformCoreObject.transform;
		(Mesh mesh, Vector2[] collisionVerts)   = platformData.Create();

		MeshFilter filter                       = gameObject.AddComponent<MeshFilter>();
		filter.sharedMesh                       = mesh;

		MeshRenderer renderer                   = gameObject.AddComponent<MeshRenderer>();
		renderer.sharedMaterial                 = assetDataPlayer.PlatformMaterial;

		PolygonCollider2D collider              = gameObject.AddComponent<PolygonCollider2D>();
		collider.points                         = collisionVerts;

		m_Platforms.Add(new Platform {_Data = platformData, _GameObject = gameObject});
	}

	////////////////////////////////////////////////////////////////
	
	void TickPlatforms()
	{
		for (int i = 0; i < m_Platforms.Count; i++)
		{
			m_Platforms[i]._Data.RemainingTime -= 1;
			if (m_Platforms[i]._Data.RemainingTime == 0)
			{
				// Destroy that platform
				GameObject.Destroy(m_Platforms[i]._GameObject);
				m_Platforms.RemoveAt(i);
				i--;
			}
		}
	}

	////////////////////////////////////////////////////////////////
	
	void TickSpawnPoints()
	{
		for (int i = 0; i < m_PlayerSpawnPoints.Count; i++)
		{
			if (m_PlayerSpawnPoints[i] == null)
			{
				// This happens when the scene of the spawn point is unloading but has not yet fired the "unloading done" event.
				// maybe there is a nicer way to handle this?
				continue;
			}

			if (m_PlayerSpawnPoints[i].CollidesWithPlayer(m_PlayerController))
			{
				if (m_CurrentPlayerSpawnPoint != m_PlayerSpawnPoints[i])
				{
					SetPlayerSpawnPoint(m_PlayerSpawnPoints[i]);
					//Debug.Log("SpawnPoint Reached " + m_CurrentPlayerSpawnPoint._LevelID + " - " + m_CurrentPlayerSpawnPoint.Index);
					
					SoundManager.Get().FitSoundToArea(m_CurrentPlayerSpawnPoint._AreaID);
				}
				break;
			}
		}
	}

	////////////////////////////////////////////////////////////////
	
	void SetPlayerSpawnPoint(PlayerSpawnPoint spawnPoint)
	{
		if (spawnPoint != null)
		{
			EventManager.Get().FireEvent(new SpawnPointReachedEvent(spawnPoint));

			if (m_CurrentSaveParticleSystem != null && spawnPoint._IsVizualized)
			{
				AssetDataPlayer assetDataPlayer = AssetManager.GetAssetData<AssetDataPlayer>();
				m_CurrentSaveParticleSystem.Clear();
				m_CurrentSaveParticleSystem.Play();
				m_CurrentSaveParticleSystem.transform.position = spawnPoint.transform.position + assetDataPlayer.SavePointEffectOffset;
				
				if (m_CurrentPlayerSpawnPoint != null)
				{
					EventManager.Get().FireEvent(new PlaySoundEvent(Sounds.FX_StopFirePlant,    m_CurrentPlayerSpawnPoint.gameObject));   
				}
				EventManager.Get().FireEvent(new PlaySoundEvent(Sounds.FX_FirePlant,        spawnPoint.gameObject));   
				GameCore.Get().StartCoroutine(C_FaceInLightForSaveParticleSystem());
			}
		}

		m_CurrentPlayerSpawnPoint = spawnPoint;
	}

	////////////////////////////////////////////////////////////////
   
	IEnumerator C_FaceInLightForSaveParticleSystem()
	{
		Light2D light = m_CurrentSaveParticleSystem.GetComponentInChildren<Light2D>();

		float time          = 0.0f;
		float totalTime     = 0.7f;
		float maxIntensity  = 1.1f;

		while (time < totalTime)
		{
			time += Time.deltaTime;

			light.intensity = (time / totalTime) * maxIntensity;

			yield return new WaitForEndOfFrame();
		}
	}

	////////////////////////////////////////////////////////////////
	
	void TickBlobs()
	{
		for (int i = 0; i < m_BlobHives.Count; i++)
		{
			if (!m_BlobHives[i]._Active)
			{
				continue;
			}

			m_BlobHives[i]._Hive.TickBlobMovement();

			for (int j = 0; j < m_BlobHives[i]._PloppedTimers.Length; j++)
			{
				if (m_BlobHives[i]._PloppedTimers[j] > 0)
				{
					m_BlobHives[i]._PloppedTimers[j] --;
				}

				if (m_BlobHives[i]._PloppedTimers[j] == 0)
				{
					m_BlobHives[i]._Hive.Blobs[j].TransitionToState(BlobState.Default, false);
					m_BlobHives[i]._Hive.SetRelatedObjectsActive(true);
				}
				
			}
		}
	}

	////////////////////////////////////////////////////////////////
	
	void TickResetPlants()
	{
		Camera camera           = CameraManager.Get().GetCamera();
		Vector2 cameraCollider  = CameraManager.Get().GetCameraColliderSize();

		for (int i = 0; i < m_ResetPlantSpawners.Count; i++)
		{
			if (m_ResetPlantSpawners[i] == null)
			{
				// Valid case: Level is getting unloaded but the level unloaded event did not yet happen.
				continue;
			}

			float absDistanceToCamera = Mathf.Abs(camera.transform.position.x - m_ResetPlantSpawners[i].transform.position.x);
			if (absDistanceToCamera > cameraCollider.x)
			{
				// Don't tick the spawner of it is outside of the camera!
				continue;
			}

			m_ResetPlantSpawners[i].Tick();
		}
		
		////////////////////////////////////////////////////////////////

		for (int i = 0; i < m_ResetPlantData.Count; i++)
		{
			if (m_ResetPlantData[i]._ResetPlant == null)
			{
				// Valid case: Level is getting unloaded but the level unloaded event did not yet happen.
				continue;
			}

			bool desiresToBeDestroyed = m_ResetPlantData[i]._ResetPlant.TickMovement();

			if (desiresToBeDestroyed)
			{
				// #destroySpawnedResetPlant
				GameObject.Destroy(m_ResetPlantData[i]._ResetPlant.gameObject);
				m_ResetPlantData.RemoveAt(i);
				i--;

				continue;
			}

			if (m_ResetPlantData[i]._PloppedTimer > 0)
			{
				m_ResetPlantData[i]._PloppedTimer = m_ResetPlantData[i]._PloppedTimer - 1;
			}

			if (m_ResetPlantData[i]._PloppedTimer == 0)
			{
				m_ResetPlantData[i]._ResetPlant.TransitionToState(ResetPlantState.Default, true);
				m_ResetPlantData[i]._PloppedTimer = ResetPlantData.NOT_PLOPPED;
			}
		}
	}

	////////////////////////////////////////////////////////////////
	
	void ResetBlobs(LevelID levelID)
	{
		for (int i = 0; i < m_BlobHives.Count; i++)
		{
			BlobHiveData blobHiveData = m_BlobHives[i];
			
			if (blobHiveData._Hive._LevelID != levelID)
			{
				continue;
			}
			
			// Reset States, Active & Timer
			blobHiveData._Hive.SetBlobStates(BlobState.Default);
			blobHiveData._Hive.SetRelatedObjectsActive(true);
			blobHiveData._Active = true;
			for (int j = 0; j < blobHiveData._PloppedTimers.Length; j++)
			{
				blobHiveData._PloppedTimers[j] = new GameTicks(0);
			}

			blobHiveData._Hive.ResetBlobMovements();

		}
	}

	////////////////////////////////////////////////////////////////
	
	public void RegisterSpawnedResetPlant(ResetPlantComponent resetPlant)
	{
		ResetPlantData data = new ResetPlantData
		{
			_ResetPlant = resetPlant,
			_PloppedTimer = ResetPlantData.NOT_PLOPPED,
			_Respawns = false
		};
		
		m_ResetPlantData.Add(data);
	}

	////////////////////////////////////////////////////////////////
	
	void ResetResetPlants(LevelID levelID)
	{
		for (int i = 0; i < m_ResetPlantSpawners.Count; i++)
		{
			m_ResetPlantSpawners[i].ResetSpawnTimer();
		}

		for (int i = 0; i < m_ResetPlantData.Count; i++)
		{
			ResetPlantData resetPlantData = m_ResetPlantData[i];

			if (resetPlantData._ResetPlant._LevelID != levelID)
			{
				continue;
			}

			if (!resetPlantData._Respawns)
			{
				// #destroySpawnedResetPlant
				GameObject.Destroy(resetPlantData._ResetPlant.gameObject);
				m_ResetPlantData.RemoveAt(i);
				i--;
				continue;
			}

			// Reset Timers
			resetPlantData._PloppedTimer = new GameTicks(0);
			resetPlantData._ResetPlant.TransitionToState(ResetPlantState.Default, false);

		}
	}

	////////////////////////////////////////////////////////////////
	
	void ResetPlatforms()
	{
		for (int i = 0; i < m_Platforms.Count; i++)
		{
			GameObject.Destroy(m_Platforms[i]._GameObject);
		}

		m_Platforms.Clear();
	}

	////////////////////////////////////////////////////////////////
	
	public void PopResetPlant(ResetPlantComponent resetPlant)
	{
		ResetPlantData data = m_ResetPlantData.Find((ResetPlantData rpd) => {
			return rpd._ResetPlant.Equals(resetPlant);
		});

		if (data == null)
		{
			Debug.LogError("Could not find resetPlant-to-be-plopped in playerManager " + resetPlant.gameObject.name);   
			return;
		}

		////////////////////////////////////////////////////////////////
		
		resetPlant.TransitionToState(ResetPlantState.Destroyed, false);
		
		if (!data._Respawns)
		{
			// #destroySpawnedResetPlant
			GameObject.Destroy(data._ResetPlant.gameObject);
			m_ResetPlantData.Remove(data);
		}
		else
		{
			AssetDataEntities assetDataBlobs = AssetManager.GetAssetData<AssetDataEntities>();
			data._PloppedTimer = assetDataBlobs.PloppedFor;
		}
				
		EventManager.Get().FireEvent(new PlaySoundEvent(Sounds.FX_SmallBlop, resetPlant.gameObject));   
		
		m_PlayerController.RegainDash(PlayerController.RegainDashContext.TemporaryReset);
	}

	////////////////////////////////////////////////////////////////

	public void PopBlob(BlobComponent blob)
	{
		int blobHiveIndex = -1;
		for (int i = 0; i < m_BlobHives.Count; i++)
		{
			if (!m_BlobHives[i]._Hive.Equals(blob.ParentBlobHive))
			{
				continue;
			}

			blobHiveIndex = i;
			break;
		}
		
		if (blobHiveIndex == -1)
		{
			Debug.LogError("Could not find parent blob hive in playerManager for blob-to-be-plopped " + blob.gameObject.name);   
			return;
		}
		
		BlobHiveData data = m_BlobHives[blobHiveIndex];

		////////////////////////////////////////////////////////////////
		
		AssetDataEntities assetDataBlobs = AssetManager.GetAssetData<AssetDataEntities>();
		data._PloppedTimers[blob.ParentBlobInHiveID] = 10000; //< We dont want plops to regrow. // assetDataBlobs.PloppedFor;
		
		blob.TransitionToState(BlobState.Destroyed, false);
		EventManager.Get().FireEvent(new PlaySoundEvent(Sounds.FX_SmallBlop, blob.gameObject));   
		
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		CameraManager.Get().AddTrauma(assetDataCamera.TraumaOnBlobPop);

		m_PlayerController.RegainDash(PlayerController.RegainDashContext.TemporaryReset);

		////////////////////////////////////////////////////////////////
		// Check if the hive is now deactivated

		if (data._Hive.Deactivatebale)
		{
			bool deactiveBlobHive = true;
			for (int i = 0; i < data._PloppedTimers.Length; i++)
			{
				if (data._PloppedTimers[i] == 0)
				{                      
					deactiveBlobHive = false;
					break;
				}
			}
			
			if (deactiveBlobHive)
			{
				data._Active = false;
				data._Hive.SetRelatedObjectsActive(false);
			}
		}

		////////////////////////////////////////////////////////////////
	   
		m_BlobHives[blobHiveIndex] = data;
	}

	////////////////////////////////////////////////////////////////
	
	public void OnIntroCutsceneFinished()
	{
		m_GameState = GameState.Gameplay;
		m_PlayerController.SetVisible(true);

		EventManager.Get().FireEvent(new GameplayStartedEvent());
	}

	////////////////////////////////////////////////////////////////
	////////////////////////////////////////////////////////////////
	////////////////////////////////////////////////////////////////
	
	public void CollectFallenSpirit(FallenSpiritComponent fallenSpirit)
	{
		m_PlayerController.CollectFallenSpirit(fallenSpirit.transform.position, fallenSpirit.SpiritColor, fallenSpirit.SpiritMidColor);
	}

	////////////////////////////////////////////////////////////////
	
	public bool ShouldPlayerPlaceSpiritsAtTotem()
	{
		AssetDataPlayer assetData = AssetManager.GetAssetData<AssetDataPlayer>();
		float distanceSQ = Vector2.SqrMagnitude(m_PlayerController.transform.position.xy() - m_Totem.transform.position.xy());
		
		return (distanceSQ < assetData.KeyPlaceDistance * assetData.KeyPlaceDistance);
	}

	////////////////////////////////////////////////////////////////
	
	public void PlaceSpiritsAtTotem(SpriteRenderer fallenSpiritRenderer)
	{
		m_Totem.AddSpirit(fallenSpiritRenderer);
	}

	////////////////////////////////////////////////////////////////
	
	public Vector3 GetNextSpiritTargetPosition()
	{
		return m_Totem.GetNextSpiritTargetPositionQueueStateIncrement();
	}

	////////////////////////////////////////////////////////////////

	////////////////////////////////////////////////////////////////

	void CreateSpawnPointEffectObject()
	{
		AssetDataPlayer assetDataPlayer                 = AssetManager.GetAssetData<AssetDataPlayer>();
		m_CurrentSaveParticleSystem                     = GameObject.Instantiate(assetDataPlayer.FirePlantParticleSystem).GetComponent<ParticleSystem>();
		m_CurrentSaveParticleSystem.transform.parent    = GameCore.Get().transform;
		m_CurrentSaveParticleSystem.transform.position  = Vector3.zero;
	}

	////////////////////////////////////////////////////////////////
	
	#if UNITY_EDITOR
	[UnityEditor.MenuItem("Plokoth/Update Prefabs")]
	public static void UpdatePrefabs()
	{
		GameObject[] gameObjectsToReplace = System.Array.FindAll(UnityEditor.Selection.gameObjects, (GameObject gameObject) => {
			return gameObject.name.Contains("BB_Light");
		});

		for (int i = 0; i < gameObjectsToReplace.Length; i++)
		{

			GameObject objectToReplace          = gameObjectsToReplace[i];
			string suffix                       = objectToReplace.name.Replace("BB_Light", "");
			suffix                              = suffix.Split(' ')[0];

			string path                         = "Assets/Prefabs/BuildingBlocks/BB_Dark/BB_Dark" + suffix + ".prefab";
			Debug.Log("Replacing Asset " + objectToReplace.name + " with " + path);
			Object newObject                    = UnityEditor.AssetDatabase.LoadAssetAtPath(path, typeof(Object));
			GameObject spawnedObject            = UnityEditor.PrefabUtility.InstantiatePrefab(newObject, objectToReplace.transform.parent) as GameObject;
			spawnedObject.transform.position    = objectToReplace.transform.position;
			spawnedObject.transform.rotation    = objectToReplace.transform.rotation;
			spawnedObject.transform.localScale  = objectToReplace.transform.localScale;
			//spawnedObject.transform.parent  = objectToReplace.transform.parent;

			UnityEditor.Undo.RegisterCreatedObjectUndo(spawnedObject, "Replace With Prefabs");
			UnityEditor.EditorUtility.SetDirty(objectToReplace.transform.parent);
			UnityEditor.Undo.DestroyObjectImmediate(objectToReplace);
		}
	}

	#endif

	////////////////////////////////////////////////////////////////
	
	public PlayerSpawnPoint GetCurrentPlayerSpawnPoint()
	{
		return m_CurrentPlayerSpawnPoint;
	}
}
