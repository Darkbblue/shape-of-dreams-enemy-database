// LOCAL SANITY-CHECK STUBS ONLY — compiled into a fake "UnityEngine.CoreModule.dll".
// Minimal Unity surface for type-checking the mod source against Dew.Core.dll/Dew.Contents.dll
// without a full Managed folder. Member bodies are intentionally fake.
using System;

namespace UnityEngine
{
	public interface ILogger
	{
		bool logEnabled
		{
			get;
			set;
		}
	}

	public class Object
	{
		public string name
		{
			get;
			set;
		}

		public static implicit operator bool(Object exists)
		{
			return exists != null;
		}

		public static bool operator ==(Object a, Object b)
		{
			return ReferenceEquals(a, b);
		}

		public static bool operator !=(Object a, Object b)
		{
			return !ReferenceEquals(a, b);
		}

		public override bool Equals(object other)
		{
			return ReferenceEquals(this, other);
		}

		public override int GetHashCode()
		{
			return 0;
		}
	}

	public class Component : Object
	{
		public T GetComponent<T>()
		{
			return default(T);
		}

		public T GetComponentInChildren<T>(bool includeInactive)
		{
			return default(T);
		}

		public T[] GetComponentsInChildren<T>(bool includeInactive)
		{
			return new T[0];
		}
	}

	public class MonoBehaviour : Component
	{
		public Coroutine StartCoroutine(System.Collections.IEnumerator routine)
		{
			return null;
		}
	}

	public class Coroutine
	{
	}

	public class GameObject : Object
	{
		public T GetComponent<T>()
		{
			return default(T);
		}

		public T GetComponentInChildren<T>(bool includeInactive)
		{
			return default(T);
		}

		public T[] GetComponentsInChildren<T>(bool includeInactive)
		{
			return new T[0];
		}
	}

	public class ScriptableObject : Object
	{
	}

	public struct Vector3
	{
		public float x;

		public float y;

		public float z;

		public static Vector3 zero => default(Vector3);

		public float sqrMagnitude => 0f;

		public static Vector3 operator -(Vector3 a, Vector3 b)
		{
			return default(Vector3);
		}
	}

	public struct Quaternion
	{
		public static Quaternion identity => default(Quaternion);
	}

	public struct Rect
	{
		public Rect(float x, float y, float width, float height)
		{
		}
	}

	public struct Color
	{
		public Color(float r, float g, float b, float a)
		{
		}

		public static Color white => default(Color);

		public static Color black => default(Color);

		public static Color yellow => default(Color);
	}

	public class GUIStyle
	{
		public GUIStyle()
		{
		}

		public GUIStyle(GUIStyle other)
		{
		}

		public int fontSize;
	}

	public class GUISkin
	{
		public GUIStyle label => new GUIStyle();
	}

	public static class GUI
	{
		public static GUISkin skin => new GUISkin();

		public static Color color
		{
			get;
			set;
		}

		public static void Label(Rect position, string text)
		{
		}

		public static void Label(Rect position, string text, GUIStyle style)
		{
		}
	}

	public static class Debug
	{
		public static UnityEngine.ILogger unityLogger => null;

		public static void Log(object message)
		{
		}

		public static void LogWarning(object message)
		{
		}

		public static void LogError(object message)
		{
		}

		public static void LogException(Exception exception)
		{
		}
	}

	public static class Time
	{
		public static float unscaledTime => 0f;

		public static float deltaTime => 0f;
	}

	public static class Mathf
	{
		public static float Min(float a, float b)
		{
			return (a < b) ? a : b;
		}
	}

	public class AsyncOperation
	{
		public bool isDone => true;
	}
}

namespace UnityEngine.SceneManagement
{
	public enum LoadSceneMode
	{
		Single,
		Additive
	}

	public struct Scene
	{
		public bool isLoaded => true;

		public string name => "";

		public GameObject[] GetRootGameObjects()
		{
			return new GameObject[0];
		}
	}

	public static class SceneManager
	{
		public static int sceneCountInBuildSettings => 0;

		public static UnityEngine.AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode)
		{
			return null;
		}

		public static UnityEngine.AsyncOperation UnloadSceneAsync(string sceneName)
		{
			return null;
		}

		public static Scene GetSceneByName(string sceneName)
		{
			return default(Scene);
		}
	}

	public static class SceneUtility
	{
		public static string GetScenePathByBuildIndex(int buildIndex)
		{
			return "";
		}
	}
}
