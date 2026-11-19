#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public static class ARColorBlasterSetup
{
    [MenuItem("AR Color Blaster/Create or Repair AR Scene")]
    public static void CreateScene()
    {
        if (!EditorUtility.DisplayDialog("Create AR Color Blaster Scene",
            "This creates a clean AR scene with AR Session, XR Origin, AR camera, plane detection, raycasting and the game controller. Save any open work first. Continue?",
            "Create Scene", "Cancel")) return;

        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var session=new GameObject("AR Session");
        session.AddComponent<ARSession>();

        var originGO=new GameObject("XR Origin");
        var origin=originGO.AddComponent<XROrigin>();
        var offset=new GameObject("Camera Offset").transform;
        offset.SetParent(originGO.transform,false);
        var cameraGO=new GameObject("Main Camera",typeof(Camera),typeof(AudioListener));
        cameraGO.tag="MainCamera";
        cameraGO.transform.SetParent(offset,false);
        cameraGO.transform.localPosition=Vector3.zero;
        cameraGO.transform.localRotation=Quaternion.identity;
        var camera=cameraGO.GetComponent<Camera>();
        camera.clearFlags=CameraClearFlags.SolidColor;
        camera.backgroundColor=Color.black;
        origin.Camera=camera;
        origin.CameraFloorOffsetObject=offset.gameObject;

        cameraGO.AddComponent<ARCameraManager>();
        cameraGO.AddComponent<ARCameraBackground>();
        originGO.AddComponent<ARPlaneManager>();
        originGO.AddComponent<ARRaycastManager>();
        cameraGO.AddComponent<ARColorBlasterController>();

        var planeManager=originGO.GetComponent<ARPlaneManager>();
        planeManager.requestedDetectionMode=PlaneDetectionMode.Horizontal|PlaneDetectionMode.Vertical;

        var lightGO=new GameObject("Directional Light");
        var light=lightGO.AddComponent<Light>();
        light.type=LightType.Directional;
        light.intensity=1.0f;
        lightGO.transform.rotation=Quaternion.Euler(50,-30,0);

        string folder="Assets/ARColorBlaster3D/Scenes";
        if(!AssetDatabase.IsValidFolder(folder))
        {
            if(!AssetDatabase.IsValidFolder("Assets/ARColorBlaster3D"))
                AssetDatabase.CreateFolder("Assets","ARColorBlaster3D");
            AssetDatabase.CreateFolder("Assets/ARColorBlaster3D","Scenes");
        }
        EditorSceneManager.SaveScene(scene,folder+"/ARColorBlaster.unity");
        EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(folder+"/ARColorBlaster.unity",true)};
        Selection.activeGameObject=originGO;
        EditorUtility.DisplayDialog("AR Scene Created",
            "Scene saved to Assets/ARColorBlaster3D/Scenes/ARColorBlaster.unity and added to Build Settings. Confirm XR Plug-in Management has ARCore enabled for Android, then build to a supported phone.",
            "OK");
    }
}
#endif
