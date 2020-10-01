using System.Collections.Generic;
using System.IO;
using System;
using MLAPI.Messaging;
using MLAPI.Serialization.Pooled;
using UnityEngine;
using MLAPI.Logging;

namespace MLAPI.Prototyping
{
	/// <summary>
	/// A prototype component for syncing transforms
	/// </summary>
	[AddComponentMenu("MLAPI/NetworkedTransform")]
	public class NetworkedTransform : NetworkedBehaviour
	{
		[Serializable]
		public enum TransformType
		{
			Transform3D,
			Transform2D,
		}

		internal class ClientSendInfo
		{
			public ulong clientId;
			public float lastSent;
			public Vector3? lastMissedPosition;
			public Quaternion? lastMissedRotation;
		}

		/// <summary>
		/// Should the server send position data
		/// </summary>
		public bool SyncPosition = true;
		/// <summary>
		/// Should the server send rotation data
		/// </summary>
		public bool SyncRotation = true;
		/// <summary>
		/// Should the rotation use all axis or just Y
		/// </summary>
		public bool FullRotation = false;
		/// <summary>
		/// The type of transform the server should send
		/// </summary>
		[SerializeField]
		public TransformType TransformTypeToSync = TransformType.Transform3D;
		/// <summary>
		/// Is the sends per second assumed to be the same across all instances
		/// </summary>
		[Tooltip("This assumes that the SendsPerSecond is synced across clients")]
		public bool AssumeSyncedSends = true;
		/// <summary>
		/// Enable interpolation
		/// </summary>
		[Tooltip("This requires AssumeSyncedSends to be true")]
		public bool InterpolatePosition = true;
		/// <summary>
		/// The distance before snaping to the position
		/// </summary>
		[Tooltip("The transform will snap if the distance is greater than this distance")]
		public float SnapDistance = 10f;
		/// <summary>
		/// The min meters to move before a send is sent
		/// </summary>
		public float MinMeters = 0.15f;
		public bool EnableMinMeters = false;
		/// <summary>
		/// The min degrees to rotate before a send it sent
		/// </summary>
		public float MinDegrees = 1.5f;
		public bool EnableMinDegrees = false;
		/// <summary>
		/// Enables extrapolation
		/// </summary>
		public bool ExtrapolatePosition = false;
		/// <summary>
		/// The maximum amount of expected send rates to extrapolate over when awaiting new packets.
		/// A higher value will result in continued extrapolation after an object has stopped moving
		/// </summary>
		public float MaxSendsToExtrapolate = 5;
		/// <summary>
		/// The channel to send the data on
		/// </summary>
		[Tooltip("The channel to send the data on. Uses the default channel (UnreliableOrdered) if left unspecified.")]
		public string Channel = null;

		private float lerpT;
		private Vector3 lerpStartPos;
		private Quaternion lerpStartRot;
		private Vector3 lerpEndPos;
		private Quaternion lerpEndRot;

		private Vector3 lastSentPos;
		private Quaternion lastSentRot;

		private float lastRecieveTime;

		/// <summary>
		/// Enables min distance required to send to other clients
		/// </summary>
		public bool EnableMinDistanceBetweenClients = true;
		public float MinDistanceBetweenClients = 22;

		/// <summary>
		/// Enables velocity based send rate
		/// </summary>
		public bool EnableVelocitySendRate;
		/// <summary>
		/// Checks for missed sends without provocation. Provocation being a client inside it's normal SendRate
		/// </summary>
		public bool EnableNonProvokedResendChecks;
		/// <summary>
		/// The curve to use to calculate the send rate
		/// </summary>
		public AnimationCurve VelocitySendRate = AnimationCurve.Constant(0, 500, 20);
		private readonly Dictionary<ulong, ClientSendInfo> clientSendInfo = new Dictionary<ulong, ClientSendInfo>();

		private void OnValidate()
		{
			if (!AssumeSyncedSends && InterpolatePosition)
				InterpolatePosition = false;
			if (MinDistanceBetweenClients < 0)
				MinDistanceBetweenClients = 0;
			if (MinDegrees < 0)
				MinDegrees = 0;
			if (MinMeters < 0)
				MinMeters = 0;
			if (EnableMinDistanceBetweenClients)
			{
				EnableMinDegrees = false;
				EnableMinMeters = false; // If we don't receive data of a client that is staying still and he was out of range, we will never see him
			}
			if (EnableNonProvokedResendChecks && !EnableVelocitySendRate)
				EnableNonProvokedResendChecks = false;
		}

		private float GetTimeForLerp(Vector3 pos1, Vector3 pos2)
		{
			return 1f / VelocitySendRate.Evaluate(Vector3.Distance(pos1, pos2));
		}

		/// <summary>
		/// Registers message handlers
		/// </summary>
		public override void NetworkStart()
		{
			lastSentRot = transform.rotation;
			lastSentPos = transform.position;

			lerpStartPos = transform.position;
			lerpStartRot = transform.rotation;

			lerpEndPos = transform.position;
			lerpEndRot = transform.rotation;
		}

		private void OnEnable()
		{
			if (NetworkingManager.Singleton != null)
				NetworkingManager.Singleton.OnNetworkedTransformUpdate += SendData;
		}
		private void OnDisable()
		{
			if (NetworkingManager.Singleton != null)
				NetworkingManager.Singleton.OnNetworkedTransformUpdate -= SendData;
		}

		private void SendData()
		{
			if (IsServer)
			{
				if ((SyncPosition && (!EnableMinMeters || Vector3.Distance(transform.position, lastSentPos) > MinMeters)) || ((SyncRotation && (!EnableMinDegrees || Quaternion.Angle(transform.rotation, lastSentRot) > MinDegrees))))
				{
					lastSentPos = transform.position;
					lastSentRot = transform.rotation;

					if (IsServer)
						InvokeApplyTransformOnEveryone(transform.position, transform.rotation, string.IsNullOrEmpty(Channel) ? "MLAPI_SERVER_TICK" : Channel);
				}
			}
		}
		private void Update()
		{
			if (!IsServer && InterpolatePosition)
			{
				if (Vector3.Distance(transform.position, lerpEndPos) > SnapDistance)
				{
					//Snap, set T to 1 (100% of the lerp)
					lerpT = 1f;
				}

				float sendDelay = (!EnableVelocitySendRate || !SyncPosition || !AssumeSyncedSends || NetworkingManager.Singleton.ConnectedClients[NetworkingManager.Singleton.LocalClientId].PlayerObject == null) ? (1f / NetworkingManager.Singleton.NetworkConfig.NetworkedTransformTickrate) : GetTimeForLerp(transform.position, NetworkingManager.Singleton.ConnectedClients[NetworkingManager.Singleton.LocalClientId].PlayerObject.transform.position);
				lerpT += Time.unscaledDeltaTime / sendDelay;

				if (SyncPosition)
				{
					if (ExtrapolatePosition && Time.unscaledTime - lastRecieveTime < sendDelay * MaxSendsToExtrapolate)
						transform.position = Vector3.LerpUnclamped(lerpStartPos, lerpEndPos, lerpT);
					else
						transform.position = Vector3.Lerp(lerpStartPos, lerpEndPos, lerpT);
				}
				if (SyncRotation)
				{
					if (ExtrapolatePosition && Time.unscaledTime - lastRecieveTime < sendDelay * MaxSendsToExtrapolate)
						transform.rotation = Quaternion.SlerpUnclamped(lerpStartRot, lerpEndRot, lerpT);
					else
						transform.rotation = Quaternion.Slerp(lerpStartRot, lerpEndRot, lerpT);
				}
			}

			if (IsServer && SyncPosition && EnableVelocitySendRate && EnableNonProvokedResendChecks) CheckForMissedSends();
		}

		void InvokeApplyTransform(ulong clientId, Vector3 position, Quaternion rotation, string channelName)
		{
			using (PooledBitStream stream = PooledBitStream.Get())
			{
				using (PooledBitWriter writer = PooledBitWriter.Get(stream))
				{
					if (TransformTypeToSync == TransformType.Transform2D)
					{
						if (SyncPosition && SyncRotation)
						{
							writer.WriteVector2Packed(position);
							writer.WriteSinglePacked(rotation.eulerAngles.z);
						}
						else if (SyncPosition)
						{
							writer.WriteVector2Packed(position);
						}
						else if (SyncRotation)
						{
							writer.WriteSinglePacked(rotation.eulerAngles.z);
						}

						InvokeClientRpcOnClientPerformance("ApplyTransform2D", clientId, stream, channelName, Security.SecuritySendFlags.None);
					}
					else if (TransformTypeToSync == TransformType.Transform3D)
					{
						if (SyncPosition && SyncRotation)
						{
							writer.WriteVector3Packed(position);
							if (FullRotation)
								writer.WriteVector3Packed(rotation.eulerAngles);
							else
								writer.WriteSingle(rotation.eulerAngles.y);
						}
						else if (SyncPosition)
						{
							writer.WriteVector3Packed(position);
						}
						else if (SyncRotation)
						{
							if (FullRotation)
								writer.WriteVector3Packed(rotation.eulerAngles);
							else
								writer.WriteSingle(rotation.eulerAngles.y);
						}

						InvokeClientRpcOnClientPerformance("ApplyTransform", clientId, stream, channelName, Security.SecuritySendFlags.None);
					}
				}
			}
		}
		void InvokeApplyTransformOnEveryone(Vector3 position, Quaternion rotation, string channelName)
		{
			using (PooledBitStream stream = PooledBitStream.Get())
			{
				using (PooledBitWriter writer = PooledBitWriter.Get(stream))
				{
					if (TransformTypeToSync == TransformType.Transform2D)
					{
						if (SyncPosition && SyncRotation)
						{
							writer.WriteVector2Packed(position);
							writer.WriteSinglePacked(rotation.eulerAngles.z);
						}
						else if (SyncPosition)
						{
							writer.WriteVector2Packed(position);
						}
						else if (SyncRotation)
						{
							writer.WriteSinglePacked(rotation.eulerAngles.z);
						}
					}
					else if (TransformTypeToSync == TransformType.Transform3D)
					{
						if (SyncPosition && SyncRotation)
						{
							writer.WriteVector3Packed(position);
							if (FullRotation)
								writer.WriteVector3Packed(rotation.eulerAngles);
							else
								writer.WriteSinglePacked(rotation.eulerAngles.y);
						}
						else if (SyncPosition)
						{
							writer.WriteVector3Packed(position);
						}
						else if (SyncRotation)
						{
							if (FullRotation)
								writer.WriteVector3Packed(rotation.eulerAngles);
							else
								writer.WriteSinglePacked(rotation.eulerAngles.y);
						}
					}

					if (EnableMinDistanceBetweenClients)
					{
						Vector3? senderPosition = transform.position;

						for (int i = 0; i < NetworkingManager.Singleton.ConnectedClientsList.Count; i++)
						{
							Vector3? receiverPosition = NetworkingManager.Singleton.ConnectedClientsList[i].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClientsList[i].PlayerObject.transform.position);
							if (receiverPosition == null || senderPosition == null || Vector3.Distance((Vector3)senderPosition, (Vector3)receiverPosition) < MinDistanceBetweenClients)
								InvokeClientRpcOnClientPerformance(TransformTypeToSync == TransformType.Transform2D ? "ApplyTransform2D" : "ApplyTransform", NetworkingManager.Singleton.ConnectedClientsList[i].ClientId, stream, channelName, Security.SecuritySendFlags.None);
						}
					}
					else
						InvokeClientRpcOnEveryonePerformance(TransformTypeToSync == TransformType.Transform2D ? "ApplyTransform2D" : "ApplyTransform", stream, channelName, Security.SecuritySendFlags.None);
				}
			}
		}

		[ClientRPC]
		private void ApplyTransform(ulong clientId, Stream stream)
		{
			if (!enabled) return;

			using (PooledBitReader reader = PooledBitReader.Get(stream))
			{
				Vector3 position = Vector3.zero;
				Quaternion rotation = Quaternion.identity;
				if (SyncPosition)
					position = reader.ReadVector3Packed();
				if (SyncRotation)
				{
					if (FullRotation)
						Quaternion.Euler(reader.ReadVector3Packed());
					else
						Quaternion.Euler(0, reader.ReadSinglePacked(), 0);
				}

				if (InterpolatePosition && !IsServer)
				{
					lastRecieveTime = Time.unscaledTime;
					if (SyncPosition)
					{
						lerpStartPos = transform.position;
						lerpEndPos = position;
					}
					if (SyncRotation)
					{
						lerpStartRot = transform.rotation;
						lerpEndRot = rotation;
					}
					lerpT = 0;
				}
				else
				{
					if (SyncPosition)
						transform.position = position;
					if (SyncRotation)
						transform.rotation = rotation;
				}
			}
		}

		[ClientRPC]
		private void ApplyTransform2D(ulong clientId, Stream stream)
		{
			if (!enabled) return;

			using (PooledBitReader reader = PooledBitReader.Get(stream))
			{
				Vector2 position = Vector2.zero;
				float rotation = 0;

				if (SyncPosition)
					position = reader.ReadVector2Packed();
				if (SyncRotation)
					rotation = reader.ReadSinglePacked();

				if (InterpolatePosition && !IsServer)
				{
					lastRecieveTime = Time.unscaledTime;
					if (SyncPosition)
					{
						lerpStartPos = transform.position;
						lerpEndPos = position;
					}
					if (SyncRotation)
					{
						lerpStartRot = transform.rotation;
						lerpEndRot = Quaternion.Euler(0, 0, rotation);
					}
					lerpT = 0;
				}
				else
				{
					if (SyncPosition)
						transform.position = position;
					if (SyncRotation)
						transform.rotation = Quaternion.Euler(0, 0, rotation);
				}
			}
		}

		private void CheckForMissedSends()
		{
			Vector3 senderPosition = transform.position;

			for (int i = 0; i < NetworkingManager.Singleton.ConnectedClientsList.Count; i++)
			{
				if (!clientSendInfo.ContainsKey(NetworkingManager.Singleton.ConnectedClientsList[i].ClientId))
				{
					clientSendInfo.Add(NetworkingManager.Singleton.ConnectedClientsList[i].ClientId, new ClientSendInfo()
					{
						clientId = NetworkingManager.Singleton.ConnectedClientsList[i].ClientId,
						lastMissedPosition = null,
						lastMissedRotation = null,
						lastSent = 0
					});
				}
				ClientSendInfo info = clientSendInfo[NetworkingManager.Singleton.ConnectedClientsList[i].ClientId];
				Vector3? receiverPosition = NetworkingManager.Singleton.ConnectedClientsList[i].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClientsList[i].PlayerObject.transform.position);

				if (receiverPosition == null || NetworkingManager.Singleton.NetworkTime - info.lastSent >= GetTimeForLerp(receiverPosition.Value, senderPosition))
				{
					Vector3? pos = null;
					if (SyncPosition)
					{
						pos = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.position);
					}
					Quaternion? rot = null;
					if (SyncRotation)
					{
						rot = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Quaternion?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.rotation);
					}

					if ((pos != null || !SyncPosition) && (rot != null || !SyncRotation))
					{
						info.lastSent = NetworkingManager.Singleton.NetworkTime;
						info.lastMissedPosition = null;
						info.lastMissedRotation = null;

						InvokeApplyTransform(NetworkingManager.Singleton.ConnectedClientsList[i].ClientId, pos.Value, rot.Value, string.IsNullOrEmpty(Channel) ? "MLAPI_SERVER_TICK" : Channel);
					}
				}
			}
		}

		/// <summary>
		/// Teleports the transform to the given position and rotation
		/// </summary>
		/// <param name="position">The position to teleport to</param>
		/// <param name="rotation">The rotation to teleport to</param>
		public void Teleport(Vector3 position, Quaternion rotation)
		{
			if (IsClient)
			{
				if (SyncPosition)
				{
					lerpStartPos = position;
					lerpEndPos = position;
				}
				if (SyncRotation)
				{
					lerpStartRot = rotation;
					lerpEndRot = rotation;
				}
				lerpT = 0;
			}
		}
	}
}
