using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ParallaxManager : IManager
{
	private AreaID  m_CurrentAreaID;
	private Vector2 m_CurrentAreaDimensionsWS;
	private Vector2 m_CurrentAreaRootWS;

	// Created Objects
	private GameObject              m_ParallaxCoreObject;
	private List<ParallaxLayer>     m_ParallaxLayers;
	
	class ParallaxLayer
	{
		public SpriteRenderer  Renderer;
		public Vector2         DimensionsWS;
	}

	public static ParallaxManager Get()
	{
		return (ParallaxManager) GameCore.Get().GetManager(ManagerType.Camera);
	}

	////////////////////////////////////////////////////////////////

	public override void Initialize()
	{
		JSDK.Events.EventManager.Get().AddListener<SpawnPointReachedEvent>(OnSpawnPointReachedEvent);

		m_ParallaxLayers        = new List<ParallaxLayer>();
		m_CurrentAreaID         = (AreaID) 123456;
		SetupParallaxLayers();
	}

	////////////////////////////////////////////////////////////////
	
	void SetupParallaxLayers()
	{
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();
		SceneContext context = SceneContext.MetaContext();

		m_ParallaxCoreObject = new GameObject("~ ParallaxRenderer");
		m_ParallaxCoreObject.transform.parent = GameCore.Get().transform;

		for (int i = 0; i < assetDataCamera._ParallaxMaxLayers; i++)
		{
			GameObject parallaxObject       = new GameObject("Layer " + i);
			parallaxObject.transform.parent = m_ParallaxCoreObject.transform;
			SpriteRenderer spriteRenderer   = parallaxObject.AddComponent<SpriteRenderer>();
			spriteRenderer.sprite           = null;
			spriteRenderer.sortingLayerName = "Background"; 
			spriteRenderer.sortingOrder     = i;
			spriteRenderer.drawMode         = SpriteDrawMode.Tiled;
			spriteRenderer.tileMode         = SpriteTileMode.Continuous;

			ParallaxLayer layer = new ParallaxLayer()
			{
				Renderer            = spriteRenderer,
				DimensionsWS        = Vector2.one
			};

			m_ParallaxLayers.Add(layer);
		}
			
		context.Unset();
	}


	////////////////////////////////////////////////////////////////

	public override void Uninitialize()
	{
		JSDK.Events.EventManager.Get().RemoveListener<SpawnPointReachedEvent>(OnSpawnPointReachedEvent);
	}

	////////////////////////////////////////////////////////////////
	
	public void OnSpawnPointReachedEvent(SpawnPointReachedEvent e)
	{
		if (e.SpawnPoint._AreaID == m_CurrentAreaID)
		{
			return;
		}

		////////////////////////////////////////////////////////////////
		
		SwitchParallaxLayersToArea(e.SpawnPoint._AreaID, e.SpawnPoint._LevelID);
	}

	////////////////////////////////////////////////////////////////
	
	public void SwitchParallaxLayersToArea(AreaID newAreaID, LevelID rootLevel)
	{
		AssetDataCamera assetDataCamera = AssetManager.GetAssetData<AssetDataCamera>();

		bool success = assetDataCamera._ParallaxLayerDictionary.TryGetValue(newAreaID, out AssetDataCamera.ParallaxAreaData parallaxAreaData);
		if (!success || parallaxAreaData._LayerDatas.Count == 0)
		{
			Debug.LogError("Could not find parallaxAreaData dictionary entry for area " + newAreaID.ToString());
			return;
		}
		

		////////////////////////////////////////////////////////////////
	   
		Debug.Assert(parallaxAreaData._LayerDatas.Count <= assetDataCamera._ParallaxMaxLayers, "Area " + newAreaID.ToString() + " defines more parallax layers than _ParallaxMaxLayers allows. Consider changing that!");

		Vector2 cameraDimensionsWS = new Vector2(16.0f / 9.0f * assetDataCamera.SizeY, assetDataCamera.SizeY);

		{
			SceneContext metaContext = SceneContext.MetaContext();
			
			m_CurrentAreaRootWS         = parallaxAreaData.generated_AreaBoundsForParallax.position;
			m_CurrentAreaDimensionsWS   = parallaxAreaData.generated_AreaBoundsForParallax.size;

			for (int i = 0; i < assetDataCamera._ParallaxMaxLayers; i++)
			{
				if (parallaxAreaData._LayerDatas.Count <= i)
				{
					m_ParallaxLayers[i].Renderer.sprite     = null;
					m_ParallaxLayers[i].Renderer.size       = Vector2.one;
					m_ParallaxLayers[i].DimensionsWS          = Vector2.one;
				}
				else
				{
					/*
					 * Parallax Images come in a 4k by 4k format
					 * They always make up 100% of their parallax layers height
					 * They tile in x-Direction
					 * 
					 * Parallax Layers are sized by multiplying the area size with a factor
					 * Then, a parallax renderer is scaled up to make sure that the one sprite it renders fills out the full layer.
					 * This scales influence is only for the renderer, all other logic stays the same.
					 */

					AssetDataCamera.ParallaxLayerData layerData = parallaxAreaData._LayerDatas[i];

					m_ParallaxLayers[i].Renderer.sprite     = layerData._Sprite;

					if (layerData._Sprite == null)
					{
						continue;
					}

                    Vector2 coveredDimensionsWS = Vector2.one;
                    switch (layerData._OrientLayerSizeAt)
                    {
                        case AssetDataCamera.ParallaxLayerData.OrientSizeAt.Area:
                            coveredDimensionsWS = m_CurrentAreaDimensionsWS * layerData._LayerToAreaSizeRatio; break;
                        case AssetDataCamera.ParallaxLayerData.OrientSizeAt.Screen:
                            coveredDimensionsWS = cameraDimensionsWS * layerData._LayerToScreenSizeRatio; break;
                        default:
                            Debug.LogError("Did you miss a new enum entry here?");
                            break;
                    }

					float rendererScaleFactor               = coveredDimensionsWS.y / cameraDimensionsWS.y;
					
					m_ParallaxLayers[i].Renderer.transform.localScale = Vector3.one * rendererScaleFactor;
					
					// > Make it so that the sprite renderer renders the sprite exactly once in y axis
					// > Then, we can just set the width to how wide (/scaleFactor) we want to be and tiling will do the rest for us.
                    Vector2 rendererSize                    = new Vector2(coveredDimensionsWS.x / rendererScaleFactor, layerData._Sprite.texture.height / layerData._Sprite.pixelsPerUnit);

					m_ParallaxLayers[i].Renderer.size       = rendererSize;
					m_ParallaxLayers[i].DimensionsWS        = rendererSize * rendererScaleFactor;
				}
			}

			metaContext.Unset();
		}

		m_CurrentAreaID = newAreaID; 

		UpdateParallaxLayerPositions();
	}

	////////////////////////////////////////////////////////////////
	
	void UpdateParallaxLayerPositions()
	{
		// Parallax Layers tile in X Direction, but not in Y Direction.
		// Example:
		// Area 1 is about 21 screens x 6 screens.
		// The backlayer of the parallax should span about 10.5 screens x 3 screens.
		// The layer itself is made out of a single texture that spans 2 screens x 3 screens.
		
		CameraManager cameraManager = CameraManager.Get();
		Camera camera               = cameraManager.GetCamera();
		Vector2 cameraSize          = cameraManager.GetCameraColliderSize();
		
        ////////////////////////////////////////////////////////////////
        // DEBUG DRAWNG AREA OUTLINES

		//DebugDrawingInterface.DrawPersistentLine(m_CurrentAreaRootWS, m_CurrentAreaRootWS + Vector2.right * m_CurrentAreaDimensionsWS, Color.magenta, 1);
		//DebugDrawingInterface.DrawPersistentLine(m_CurrentAreaRootWS, m_CurrentAreaRootWS + Vector2.up * m_CurrentAreaDimensionsWS, Color.magenta, 1);
		//DebugDrawingInterface.DrawPersistentLine(m_CurrentAreaRootWS + m_CurrentAreaDimensionsWS, m_CurrentAreaRootWS + Vector2.right * m_CurrentAreaDimensionsWS, Color.magenta, 1);
		//DebugDrawingInterface.DrawPersistentLine(m_CurrentAreaRootWS + m_CurrentAreaDimensionsWS, m_CurrentAreaRootWS + Vector2.up * m_CurrentAreaDimensionsWS, Color.magenta, 1);

        ////////////////////////////////////////////////////////////////

		for (int i = 0; i < m_ParallaxLayers.Count; i++)
		{


			ParallaxLayer layer                 = m_ParallaxLayers[i];
			if (layer.DimensionsWS.magnitude == 0.0f)
			{
				continue;
			}

			Vector2 cameraToRootOffset          = camera.transform.position.xy() - m_CurrentAreaRootWS;

			// Find progress in this area between [0, 1]
			float progressX                     = (cameraToRootOffset.x) / (m_CurrentAreaDimensionsWS.x);
			float progressY                     = (cameraToRootOffset.y) / (m_CurrentAreaDimensionsWS.y);

			// Offset parallax layer by [0, 1] * layer.Dimensions
			float layerPositionX                = cameraToRootOffset.x - (layer.DimensionsWS.x - cameraSize.x) * progressX;
			float layerPositionY                = cameraToRootOffset.y - (layer.DimensionsWS.y - cameraSize.y) * progressY;
			
			//DebugDrawingInterface.DrawPersistentSSText("AreaDimensions:             " + m_CurrentAreaDimensionsWS.x + ", " + m_CurrentAreaDimensionsWS.y, new Vector2(150, 25), Color.white, 1);
			//DebugDrawingInterface.DrawPersistentSSText("CameraToRootOffset:         " + cameraToRootOffset.x + ", " + cameraToRootOffset.y, new Vector2(150, 65), Color.white, 1);
			//DebugDrawingInterface.DrawPersistentSSText("LayerPosition:              " + layerPositionX + ", " + layerPositionY, new Vector2(150, 105), Color.white, 1);
            
			Vector2 rendererOriginInArea        = m_CurrentAreaRootWS + (layer.DimensionsWS) / 2.0f - cameraSize / 2.0f;
            
            ////////////////////////////////////////////////////////////////
            // DEBUG DRAWING LAYER OUTLINES
            
		    //DebugDrawingInterface.DrawPersistentPointBox(rendererOriginInArea, 0.2f, Color.magenta, 1);
            //Vector2 rendererSpan   = layer.Renderer.size * layer.Renderer.transform.localScale.y;
            //Vector2 rendererOrigin = layer.Renderer.transform.position.xy() - rendererSpan / 2.0f;
		    //DebugDrawingInterface.DrawPersistentLine(rendererOrigin, rendererOrigin + Vector2.right * rendererSpan, Color.magenta, 1);
		    //DebugDrawingInterface.DrawPersistentLine(rendererOrigin, rendererOrigin + Vector2.up * rendererSpan, Color.magenta, 1);
		    //DebugDrawingInterface.DrawPersistentLine(rendererOrigin + rendererSpan, rendererOrigin + Vector2.right * rendererSpan, Color.magenta, 1);
		    //DebugDrawingInterface.DrawPersistentLine(rendererOrigin + rendererSpan, rendererOrigin + Vector2.up * rendererSpan, Color.magenta, 1);

            ////////////////////////////////////////////////////////////////

			layer.Renderer.transform.position   = rendererOriginInArea + new Vector2(layerPositionX, layerPositionY);
		}
	}
	////////////////////////////////////////////////////////////////

	public override void OnLateTick()
	{
		UpdateParallaxLayerPositions();
	}
}
