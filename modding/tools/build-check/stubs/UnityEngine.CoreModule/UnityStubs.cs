// LOCAL SANITY-CHECK STUBS ONLY — compiled into a fake "UnityEngine.CoreModule.dll".
// Minimal Unity surface for type-checking the mod source against Dew.Core.dll/Dew.Contents.dll
// without a full Managed folder. Member bodies are intentionally fake.
using System;

namespace UnityEngine
{
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

	public static class Debug
	{
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
}
