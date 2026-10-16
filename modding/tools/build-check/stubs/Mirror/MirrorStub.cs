// LOCAL SANITY-CHECK STUBS ONLY — compiled into a fake "Mirror.dll".
using UnityEngine;

namespace Mirror
{
	public class NetworkBehaviour : MonoBehaviour
	{
		public bool isServer => true;

		public bool isClient => true;
	}
}
