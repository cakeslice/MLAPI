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
		/// The type of transform the server should send
		/// </summary>
		[SerializeField]
		public TransformType TransformTypeToSync = TransformType.Transform3D;
		/// <summary>
		/// The base amount of sends per seconds to use when range is disabled
		/// </summary>
		[Range(0, 120)]
		public float FixedSendsPerSecond = 20f;
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
		/// Should the server interpolate
		/// </summary>
		public bool InterpolateServer = true;
		/// <summary>
		/// The min meters to move before a send is sent
		/// </summary>
		public float MinMeters = 0.15f;
		/// <summary>
		/// The min degrees to rotate before a send it sent
		/// </summary>
		public float MinDegrees = 1.5f;
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
		[Tooltip("The channel to send the data on. Uses the default channel if left unspecified")]
		public string Channel = null;

		private float lerpT;
		private Vector3 lerpStartPos;
		private Quaternion lerpStartRot;
		private Vector3 lerpEndPos;
		private Quaternion lerpEndRot;

		private float lastSendTime;
		private Vector3 lastSentPos;
		private Quaternion lastSentRot;

		private float lastRecieveTime;

		/// <summary>
		/// Enables range based send rate
		/// </summary>
		public bool EnableRange;
		/// <summary>
		/// Checks for missed sends without provocation. Provocation being a client inside it's normal SendRate
		/// </summary>
		public bool EnableNonProvokedResendChecks;
		/// <summary>
		/// The curve to use to calculate the send rate
		/// </summary>
		public AnimationCurve DistanceSendrate = AnimationCurve.Constant(0, 500, 20);
		private readonly Dictionary<ulong, ClientSendInfo> clientSendInfo = new Dictionary<ulong, ClientSendInfo>();

		/// <summary>
		/// The delegate used to check if a move is valid
		/// </summary>
		/// <param name="oldPos">The previous position</param>
		/// <param name="newPos">The new requested position</param>
		/// <returns>Returns Whether or not the move is valid</returns>
		public delegate bool MoveValidationDelegate(Vector3 oldPos, Vector3 newPos);
		/// <summary>
		/// If set, moves will only be accepted if the custom delegate returns true
		/// </summary>
		public MoveValidationDelegate IsMoveValidDelegate = null;

		private void OnValidate()
		{
			if (!AssumeSyncedSends && InterpolatePosition)
				InterpolatePosition = false;
			if (InterpolateServer && !InterpolatePosition)
				InterpolateServer = false;
			if (MinDegrees < 0)
				MinDegrees = 0;
			if (MinMeters < 0)
				MinMeters = 0;
			if (EnableNonProvokedResendChecks && !EnableRange)
				EnableNonProvokedResendChecks = false;
		}

		private float GetTimeForLerp(Vector3 pos1, Vector3 pos2)
		{
			return 1f / DistanceSendrate.Evaluate(Vector3.Distance(pos1, pos2));
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

		private void Update()
		{
			if (IsOwner)
			{
				if (NetworkingManager.Singleton.NetworkTime - lastSendTime >= (1f / FixedSendsPerSecond) && (SyncPosition && (Vector3.Distance(transform.position, lastSentPos) > MinMeters) || (SyncRotation && Quaternion.Angle(transform.rotation, lastSentRot) > MinDegrees)))
				{
					lastSendTime = NetworkingManager.Singleton.NetworkTime;
					lastSentPos = transform.position;
					lastSentRot = transform.rotation;

					if (IsServer)
						InvokeApplyTransformOnEveryoneExcept(OwnerClientId, transform.position, transform.rotation, string.IsNullOrEmpty(Channel) ? "MLAPI_DEFAULT_MESSAGE" : Channel);
					else
						InvokeServerRpc(SubmitTransform, transform.position, transform.rotation, string.IsNullOrEmpty(Channel) ? "MLAPI_DEFAULT_MESSAGE" : Channel);
				}
			}
			else
			{
				//If we are server and interpolation is turned on for server OR we are not server and interpolation is turned on
				if ((IsServer && InterpolateServer && InterpolatePosition) || (!IsServer && InterpolatePosition))
				{
					if (Vector3.Distance(transform.position, lerpEndPos) > SnapDistance)
					{
						//Snap, set T to 1 (100% of the lerp)
						lerpT = 1f;
					}

					float sendDelay = (IsServer || !EnableRange || !AssumeSyncedSends || NetworkingManager.Singleton.ConnectedClients[NetworkingManager.Singleton.LocalClientId].PlayerObject == null) ? (1f / FixedSendsPerSecond) : GetTimeForLerp(transform.position, NetworkingManager.Singleton.ConnectedClients[NetworkingManager.Singleton.LocalClientId].PlayerObject.transform.position);
					lerpT += Time.unscaledDeltaTime / sendDelay;

					if (ExtrapolatePosition && Time.unscaledTime - lastRecieveTime < sendDelay * MaxSendsToExtrapolate)
						transform.position = Vector3.LerpUnclamped(lerpStartPos, lerpEndPos, lerpT);
					else
						transform.position = Vector3.Lerp(lerpStartPos, lerpEndPos, lerpT);

					if (ExtrapolatePosition && Time.unscaledTime - lastRecieveTime < sendDelay * MaxSendsToExtrapolate)
						transform.rotation = Quaternion.SlerpUnclamped(lerpStartRot, lerpEndRot, lerpT);
					else
						transform.rotation = Quaternion.Slerp(lerpStartRot, lerpEndRot, lerpT);
				}
			}

			if (IsServer && EnableRange && EnableNonProvokedResendChecks) CheckForMissedSends();
		}

		void InvokeApplyTransform(ulong clientId, Vector3 position, Quaternion rotation, string channelName)
		{
			if (TransformTypeToSync == TransformType.Transform2D)
			{
				if (SyncPosition && SyncRotation)
					InvokeClientRpcOnClient(ApplyTransform2D, clientId, (Vector2)position, rotation.eulerAngles.z, channelName);
				else if (SyncPosition)
					InvokeClientRpcOnClient(ApplyTransform2DOnlyPosition, clientId, (Vector2)position, channelName);
				else if (SyncRotation)
					InvokeClientRpcOnClient(ApplyTransform2DOnlyRotation, clientId, rotation.eulerAngles.z, channelName);
			}
			else if (TransformTypeToSync == TransformType.Transform3D)
			{
				if (SyncPosition && SyncRotation)
					InvokeClientRpcOnClient(ApplyTransform, clientId, position, rotation, channelName);
				else if (SyncPosition)
					InvokeClientRpcOnClient(ApplyTransformOnlyPosition, clientId, position, channelName);
				else if (SyncRotation)
					InvokeClientRpcOnClient(ApplyTransformOnlyRotation, clientId, rotation, channelName);
			}
		}
		void InvokeApplyTransformOnEveryoneExcept(ulong clientId, Vector3 position, Quaternion rotation, string channelName)
		{
			if (TransformTypeToSync == TransformType.Transform2D)
			{
				if (SyncPosition && SyncRotation)
					InvokeClientRpcOnEveryoneExcept(ApplyTransform2D, clientId, (Vector2)position, rotation.eulerAngles.z, channelName);
				else if (SyncPosition)
					InvokeClientRpcOnEveryoneExcept(ApplyTransform2DOnlyPosition, clientId, (Vector2)position, channelName);
				else if (SyncRotation)
					InvokeClientRpcOnEveryoneExcept(ApplyTransform2DOnlyRotation, clientId, rotation.eulerAngles.z, channelName);
			}
			else if (TransformTypeToSync == TransformType.Transform3D)
			{
				if (SyncPosition && SyncRotation)
					InvokeClientRpcOnEveryoneExcept(ApplyTransform, clientId, position, rotation, channelName);
				else if (SyncPosition)
					InvokeClientRpcOnEveryoneExcept(ApplyTransformOnlyPosition, clientId, position, channelName);
				else if (SyncRotation)
					InvokeClientRpcOnEveryoneExcept(ApplyTransformOnlyRotation, clientId, rotation, channelName);
			}
		}

		[ClientRPC]
		private void ApplyTransform(Vector3 position, Quaternion rotation)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartPos = transform.position;
				lerpStartRot = transform.rotation;
				lerpEndPos = position;
				lerpEndRot = rotation;
				lerpT = 0;
			}
			else
			{
				transform.position = position;
				transform.rotation = rotation;
			}
		}
		[ClientRPC]
		private void ApplyTransformOnlyPosition(Vector3 position)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartPos = transform.position;
				lerpEndPos = position;
				lerpT = 0;
			}
			else
			{
				transform.position = position;
			}
		}
		[ClientRPC]
		private void ApplyTransformOnlyRotation(Quaternion rotation)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartRot = transform.rotation;
				lerpEndRot = rotation;
				lerpT = 0;
			}
			else
			{
				transform.rotation = rotation;
			}
		}
		[ClientRPC]
		private void ApplyTransform2D(Vector2 position, float rotation)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartPos = transform.position;
				lerpStartRot = transform.rotation;
				lerpEndPos = position;
				lerpEndRot = Quaternion.Euler(0, 0, rotation);
				lerpT = 0;
			}
			else
			{
				transform.position = position;
				transform.rotation = Quaternion.Euler(0, 0, rotation);
			}
		}
		[ClientRPC]
		private void ApplyTransform2DOnlyPosition(Vector2 position)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartPos = transform.position;
				lerpEndPos = position;
				lerpT = 0;
			}
			else
			{
				transform.position = position;
			}
		}
		[ClientRPC]
		private void ApplyTransform2DOnlyRotation(float rotation)
		{
			if (!enabled) return;

			if (InterpolatePosition && (!IsServer || InterpolateServer))
			{
				lastRecieveTime = Time.unscaledTime;
				lerpStartRot = transform.rotation;
				lerpEndRot = Quaternion.Euler(0, 0, rotation);
				lerpT = 0;
			}
			else
			{
				transform.rotation = Quaternion.Euler(0, 0, rotation);
			}
		}

		[ServerRPC]
		private void SubmitTransform(Vector3 position, Quaternion rotation)
		{
			if (!enabled) return;

			if (IsMoveValidDelegate != null && !IsMoveValidDelegate(lerpEndPos, position))
			{
				//Invalid move!
				//TODO: Add rubber band (just a message telling them to go back)
				return;
			}

			if (!IsClient)
			{
				// Dedicated server
				if (TransformTypeToSync == TransformType.Transform2D)
				{
					if (SyncPosition && SyncRotation)
						ApplyTransform2D(position, rotation.eulerAngles.z);
					else if (SyncPosition)
						ApplyTransform2DOnlyPosition(position);
					else if (SyncRotation)
						ApplyTransform2DOnlyRotation(rotation.eulerAngles.z);
				}
				else if (TransformTypeToSync == TransformType.Transform3D)
				{
					if (SyncPosition && SyncRotation)
						ApplyTransform(position, rotation);
					else if (SyncPosition)
						ApplyTransformOnlyPosition(position);
					else if (SyncRotation)
						ApplyTransformOnlyRotation(rotation);
				}
			}

			if (EnableRange)
			{
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
					Vector3? senderPosition = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.position);

					if ((receiverPosition == null || senderPosition == null && NetworkingManager.Singleton.NetworkTime - info.lastSent >= (1f / FixedSendsPerSecond)) || NetworkingManager.Singleton.NetworkTime - info.lastSent >= GetTimeForLerp(receiverPosition.Value, senderPosition.Value))
					{
						info.lastSent = NetworkingManager.Singleton.NetworkTime;
						info.lastMissedPosition = null;
						info.lastMissedRotation = null;

						InvokeApplyTransform(NetworkingManager.Singleton.ConnectedClientsList[i].ClientId, position, rotation, string.IsNullOrEmpty(Channel) ? "MLAPI_DEFAULT_MESSAGE" : Channel);
					}
					else
					{
						info.lastMissedPosition = position;
						info.lastMissedRotation = rotation;
					}
				}
			}
			else
			{
				InvokeApplyTransformOnEveryoneExcept(OwnerClientId, position, rotation, string.IsNullOrEmpty(Channel) ? "MLAPI_DEFAULT_MESSAGE" : Channel);
			}
		}

		private void CheckForMissedSends()
		{
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
				Vector3? senderPosition = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.position);

				if ((receiverPosition == null || senderPosition == null && NetworkingManager.Singleton.NetworkTime - info.lastSent >= (1f / FixedSendsPerSecond)) || NetworkingManager.Singleton.NetworkTime - info.lastSent >= GetTimeForLerp(receiverPosition.Value, senderPosition.Value))
				{
					Vector3? pos = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Vector3?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.position);
					Quaternion? rot = NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject == null ? null : new Quaternion?(NetworkingManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject.transform.rotation);

					if (pos != null && rot != null)
					{
						info.lastSent = NetworkingManager.Singleton.NetworkTime;
						info.lastMissedPosition = null;
						info.lastMissedRotation = null;

						InvokeApplyTransform(NetworkingManager.Singleton.ConnectedClientsList[i].ClientId, pos.Value, rot.Value, string.IsNullOrEmpty(Channel) ? "MLAPI_DEFAULT_MESSAGE" : Channel);
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
			if (InterpolateServer && IsServer || IsClient)
			{
				lerpStartPos = position;
				lerpStartRot = rotation;
				lerpEndPos = position;
				lerpEndRot = rotation;
				lerpT = 0;
			}
		}
	}
}
