using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Helper;
using Log;

namespace LoginClient
{
	public class RealmSession: IDisposable
	{
		private readonly NetworkStream networkStream;
		public int ID { get; set; }

		public RealmSession(NetworkStream networkStream)
		{
			this.networkStream = networkStream;
		}

		public void Dispose()
		{
			this.networkStream.Dispose();
		}

		public async void SendMessage<T>(ushort opcode, T message)
		{
			byte[] protoBytes = ProtobufHelper.ToBytes(message);
			var neworkBytes = new byte[sizeof (int) + sizeof (ushort) + protoBytes.Length];

			int totalSize = sizeof (ushort) + protoBytes.Length;

			var totalSizeBytes = BitConverter.GetBytes(totalSize);
			totalSizeBytes.CopyTo(neworkBytes, 0);

			var opcodeBytes = BitConverter.GetBytes(opcode);
			opcodeBytes.CopyTo(neworkBytes, sizeof (int));

			protoBytes.CopyTo(neworkBytes, sizeof (int) + sizeof (ushort));

			await this.networkStream.WriteAsync(neworkBytes, 0, neworkBytes.Length);
		}

		public async Task<SMSG_Password_Protect_Type> Handle_CMSG_AuthLogonPermit_Response()
		{
			var result = await this.RecvMessage();
			ushort opcode = result.Item1;
			byte[] message = result.Item2;

			if (opcode != MessageOpcode.SMSG_PASSWORD_PROTECT_TYPE)
			{
				throw new LoginException(string.Format(
					"session: {0}, opcode: {1}", this.ID, opcode));
			}

			var smsgPasswordProtectType = 
				ProtobufHelper.FromBytes<SMSG_Password_Protect_Type>(message);

			Logger.Trace("message: {0}", JsonHelper.ToString(smsgPasswordProtectType));

			if (smsgPasswordProtectType.Code != 200)
			{
				throw new LoginException(string.Format(
					"session: {0}, SMSG_Lock_For_Safe_Time: {1}", 
					this.ID, JsonHelper.ToString(smsgPasswordProtectType)));
			}

			return smsgPasswordProtectType;
		}

		public async Task<SMSG_Auth_Logon_Challenge_Response>
			Handle_SMSG_Auth_Logon_Challenge_Response()
		{
			var result = await this.RecvMessage();
			ushort opcode = result.Item1;
			byte[] message = result.Item2;

			if (opcode != MessageOpcode.SMSG_AUTH_LOGON_CHALLENGE_RESPONSE)
			{
				Logger.Trace("opcode: {0}", opcode);
				throw new LoginException(string.Format(
					"session: {0}, opcode: {1}", this.ID, opcode));
			}
			
			var smsgAuthLogonChallengeResponse =
				ProtobufHelper.FromBytes<SMSG_Auth_Logon_Challenge_Response>(message);

			if (smsgAuthLogonChallengeResponse.ErrorCode != ErrorCode.REALM_AUTH_SUCCESS)
			{
				Logger.Trace("error code: {0}", smsgAuthLogonChallengeResponse.ErrorCode);
				throw new LoginException(
					string.Format("session: {0}, SMSG_Auth_Logon_Challenge_Response: {1}",
					this.ID, JsonHelper.ToString(smsgAuthLogonChallengeResponse)));
			}

			Logger.Debug("SMSG_Auth_Logon_Challenge_Response: \n{0}", 
				JsonHelper.ToString(smsgAuthLogonChallengeResponse));

			return smsgAuthLogonChallengeResponse;
		}

		public async Task<SMSG_Auth_Logon_Proof_M2> Handle_SMSG_Auth_Logon_Proof_M2()
		{
			var result = await this.RecvMessage();
			ushort opcode = result.Item1;
			byte[] message = result.Item2;

			if (opcode != MessageOpcode.SMSG_AUTH_LOGON_PROOF_M2)
			{
				throw new LoginException(string.Format(
					"session: {0}, error opcode: {1}", this.ID, opcode));
			}

			var smsgAuthLogonProofM2 = ProtobufHelper.FromBytes<SMSG_Auth_Logon_Proof_M2>(message);

			if (smsgAuthLogonProofM2.ErrorCode != ErrorCode.REALM_AUTH_SUCCESS)
			{
				throw new LoginException(string.Format(
					"session: {0}, SMSG_Auth_Logon_Proof_M2: {1}", 
					this.ID, JsonHelper.ToString(smsgAuthLogonProofM2)));
			}

			return smsgAuthLogonProofM2;
		}

		public async Task<SMSG_Realm_List> Handle_SMSG_Realm_List()
		{
			var result = await this.RecvMessage();
			ushort opcode = result.Item1;
			byte[] message = result.Item2;

			if (opcode != MessageOpcode.SMSG_REALM_LIST)
			{
				throw new LoginException(string.Format(
					"session: {0}, error opcode: {1}", this.ID, opcode));
			}

			var smsgRealmList = ProtobufHelper.FromBytes<SMSG_Realm_List>(message);

			return smsgRealmList;
		}

		public async Task<Tuple<ushort, byte[]>> RecvMessage()
		{
			int totalReadSize = 0;
			int needReadSize = sizeof (int);
			var packetBytes = new byte[needReadSize];
			while (totalReadSize != needReadSize)
			{
				int readSize = await this.networkStream.ReadAsync(
					packetBytes, totalReadSize, packetBytes.Length);
				if (readSize == 0)
				{
					throw new LoginException(string.Format(
						"session: {0}, connection closed", this.ID));
				}
				totalReadSize += readSize;
			}

			int packetSize = BitConverter.ToInt32(packetBytes, 0);

			// 读opcode和message
			totalReadSize = 0;
			needReadSize = packetSize;
			var contentBytes = new byte[needReadSize];
			while (totalReadSize != needReadSize)
			{
				int readSize = await this.networkStream.ReadAsync(
					contentBytes, totalReadSize, contentBytes.Length);
				if (readSize == 0)
				{
					throw new LoginException(string.Format(
						"session: {0}, connection closed", this.ID));
				}
				totalReadSize += readSize;
			}

			ushort opcode = BitConverter.ToUInt16(contentBytes, 0);

			var messageBytes = new byte[needReadSize - sizeof (ushort)];
			Array.Copy(contentBytes, sizeof (ushort), messageBytes, 0, messageBytes.Length);

			return new Tuple<ushort, byte[]>(opcode, messageBytes);
		}

		public async Task<List<Realm_List_Gate>> Login(string account, string password)
		{
			byte[] passwordBytes = password.ToByteArray();
			MD5 md5 = MD5.Create();
			byte[] passwordMd5 = md5.ComputeHash(passwordBytes);
			byte[] passwordMd5Hex = passwordMd5.ToHex().ToLower().ToByteArray();

			// 发送帐号和密码MD5
			var cmsgAuthLogonPermit = new CMSG_Auth_Logon_Permit
			{ 
				Account = account.ToByteArray(),
				PasswordMd5 = passwordMd5Hex
			};

			Logger.Trace("account: {0}, password: {1}", 
				cmsgAuthLogonPermit.Account, cmsgAuthLogonPermit.PasswordMd5.ToStr());

			this.SendMessage(MessageOpcode.CMSG_AUTH_LOGON_PERMIT, cmsgAuthLogonPermit);
			await this.Handle_CMSG_AuthLogonPermit_Response();

			// 这个消息已经没有作用,只用来保持原有的代码流程
			var cmsgAuthLogonChallenge = new CMSG_Auth_Logon_Challenge();
			this.SendMessage(MessageOpcode.CMSG_AUTH_LOGON_CHALLENGE, cmsgAuthLogonChallenge);
			var smsgAuthLogonChallengeResponse = 
				await this.Handle_SMSG_Auth_Logon_Challenge_Response();

			// 以下是SRP6处理过程
			var n = smsgAuthLogonChallengeResponse.N.ToUBigInteger();
			var g = smsgAuthLogonChallengeResponse.G.ToUBigInteger();
			var b = smsgAuthLogonChallengeResponse.B.ToUBigInteger();
			var salt = smsgAuthLogonChallengeResponse.S.ToUBigInteger();

			var srp6Client = new SRP6Client(
				new SHA1Managed(), n, g, b, salt, account.ToByteArray(), passwordMd5Hex);

			Logger.Debug("s: {0}\nN: {1}\nG: {2}\nB: {3}\nA: {4}\nS: {5}\nK: {6}\nm: {7}",
				srp6Client.Salt.ToUBigIntegerArray().ToHex(),
				srp6Client.N.ToUBigIntegerArray().ToHex(), 
				srp6Client.G.ToUBigIntegerArray().ToHex(),
				srp6Client.B.ToUBigIntegerArray().ToHex(),
				srp6Client.A.ToUBigIntegerArray().ToHex(), 
				srp6Client.S.ToUBigIntegerArray().ToHex(),
				srp6Client.K.ToUBigIntegerArray().ToHex(), 
				srp6Client.M.ToUBigIntegerArray().ToHex());

			var cmsgAuthLogonProof = new CMSG_Auth_Logon_Proof
			{
				A = srp6Client.A.ToUBigIntegerArray(),
				M = srp6Client.M.ToUBigIntegerArray()
			};
			this.SendMessage(MessageOpcode.CMSG_AUTH_LOGON_PROOF, cmsgAuthLogonProof);
			await this.Handle_SMSG_Auth_Logon_Proof_M2();

			// 请求realm list
			var cmsgRealmList = new CMSG_Realm_List();
			this.SendMessage(MessageOpcode.CMSG_REALM_LIST, cmsgRealmList);
			var smsgRealmList = await this.Handle_SMSG_Realm_List();
			return smsgRealmList.GateList;
		}
	}
}