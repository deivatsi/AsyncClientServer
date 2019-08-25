using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleSockets.Messaging;
using SimpleSockets.Messaging.MessageContract;
using SimpleSockets.Messaging.Metadata;

namespace SimpleSockets.Server
{
	public class SimpleSocketTcpListener: SimpleSocketListener
	{
		/// <summary>
		/// Start listening on specified port and ip.
		/// <para/>The limit is the maximum amount of client which can connect at one moment. You can just fill in 'null' or "" as the ip value.
		/// That way it will automatically choose an ip to listen to. Using IPAddress.Any.
		/// </summary>
		/// <param name="ip">The ip the server will be listening to.</param>
		/// <param name="port">The port on which the server will be running.</param>
		/// <param name="limit">Optional parameter, default value is 500.</param>
		public override void StartListening(string ip, int port, int limit = 500)
		{
			if (port < 1 || port > 65535)
				throw new ArgumentOutOfRangeException(nameof(port));
			if (limit < 0)
				throw new ArgumentException("Limit cannot be under 0.");
			if (limit == 0)
				throw new ArgumentException("Limit cannot be 0.");

			Port = port;
			Ip = ip;

			var endpoint = new IPEndPoint(DetermineListenerIp(ip), port);

			TokenSource = new CancellationTokenSource();
			Token = TokenSource.Token;

			Task.Run(SendFromQueue, Token);

			Task.Run(() =>
			{
				try
				{

					using (var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
					{
						Listener = listener;
						listener.Bind(endpoint);
						listener.Listen(Limit);

						RaiseServerHasStarted();
						while (!Token.IsCancellationRequested)
						{
							CanAcceptConnections.Reset();
							listener.BeginAccept(this.OnClientConnect, listener);
							CanAcceptConnections.WaitOne();
						}

					}
				}
				catch (ObjectDisposedException ode)
				{
					RaiseErrorThrown(ode);
				}
				catch (SocketException se)
				{
					throw new Exception(se.ToString());
				}
			}, Token);

		}

		protected override void OnClientConnect(IAsyncResult result)
		{

			if (Token.IsCancellationRequested)
				return;

			CanAcceptConnections.Set();
			try
			{
				IClientMetadata state;

				lock (ConnectedClients)
				{
					var id = !ConnectedClients.Any() ? 1 : ConnectedClients.Keys.Max() + 1;

					state = new ClientMetadata(((Socket)result.AsyncState).EndAccept(result), id);


					//If the server shouldn't accept the IP do nothing.
					if (!IsConnectionAllowed(state))
						return;

					var client = ConnectedClients.FirstOrDefault(x => x.Value == state);

					if (client.Value == state)
					{
						id = client.Key;
						ConnectedClients.Remove(id);
						ConnectedClients.Add(id, state);
					}
					else
					{
						ConnectedClients.Add(id, state);
					}

					RaiseClientConnected(state);
				}

				NetworkDataReceiver(state);
			}
			catch (ObjectDisposedException ode)
			{
				RaiseErrorThrown(ode);
			}
			catch (SocketException se)
			{
				RaiseErrorThrown(se);
			}

		}

		#region Receiving


		protected override void NetworkDataReceiver(IClientMetadata state)
		{
			try
			{

				while (!Token.IsCancellationRequested)
				{
					state.MreRead.WaitOne();
					state.MreRead.Reset();

					var offset = 0;

					if (state.UnhandledBytes != null)
						offset = state.UnhandledBytes.Length;

					if (state.Buffer.Length < state.BufferSize)
					{
						state.ChangeBuffer(new byte[state.BufferSize]);
						if (offset > 0)
							Array.Copy(state.UnhandledBytes, 0, state.Buffer, 0, state.UnhandledBytes.Length);
					}

					state.Listener.BeginReceive(state.Buffer, offset, state.Buffer.Length - offset, SocketFlags.None, 
						Receiver = async (ar) =>
						{
							var client = (ClientMetadata)ar.AsyncState;
							var receive = state.Listener.EndReceive(ar);

							if (receive > 0)
							{
								//Check if client is still connected.
								//If client is disconnected, send disconnected message
								//and remove from clients list
								if (!IsConnected(state.Id))
								{
									RaiseClientDisconnected(state);
									lock (ConnectedClients)
									{
										ConnectedClients.Remove(state.Id);
									}
								}
								//Else start receiving and handle the message.
								else
								{
									receive += offset;

									//Does header check
									if (client.Flag == 0)
									{
										if (client.SimpleMessage == null)
											client.SimpleMessage = new SimpleMessage(client, this, Debug);
										await ParallelQueue.Enqueue(() =>
											client.SimpleMessage.ReadBytesAndBuildMessage(receive));
									}
									else if (receive > 0)
									{
										await ParallelQueue.Enqueue(() =>
											client.SimpleMessage.ReadBytesAndBuildMessage(receive));
									}
								}
							}

							client.MreRead.Set();
							state = client;
						}, state);
				}
			}
			catch (Exception ex)
			{
				state.Reset();
				RaiseLog(ex);
				RaiseLog("Error handling message from client with guid : " + state.Guid + ".");
				RaiseErrorThrown(ex);
				NetworkDataReceiver(state);
			}
		}
		
		#endregion

		#region Message Sending

		protected override void BeginSendFromQueue(MessageWrapper message)
		{
			try
			{
				message.State.Listener.BeginSend(message.Data, 0, message.Data.Length, SocketFlags.None, SendCallback, message);
			}
			catch (Exception ex)
			{
				RaiseMessageFailed(message.State, message.Data, ex);
			}
		}

		/// <summary>
		/// Send bytes to client.
		/// </summary>
		/// <param name="data"></param>
		/// <param name="close"></param>
		/// <param name="partial"></param>
		/// <param name="id"></param>
		protected override void SendToSocket(byte[] data, bool close,bool partial = false, int id = -1)
		{
			var state = GetClient(id);
			try
			{
				if (state != null)
				{
					if (!IsConnected(state.Id))
					{
						//Sets client with id to disconnected
						RaiseClientDisconnected(state);
						throw new Exception("Message failed to send because the destination socket is not connected.");
					}
					else
					{
						state.Close = close;
						BlockingMessageQueue.Enqueue(new MessageWrapper(data, state, partial));
					}
				}
			}
			catch (Exception ex)
			{
				RaiseMessageFailed(state, data, ex);
			}
		}

		//End the send and invoke MessageSubmitted event.
		protected override void SendCallback(IAsyncResult result)
		{
			var message = (MessageWrapper)result.AsyncState;
			var state = message.State;

			try
			{
				state.Listener.EndSend(result);
				if (!message.Partial && state.Close)
					Close(state.Id);
			}
			catch (SocketException se)
			{
				throw new SocketException(se.ErrorCode);
			}
			catch (ObjectDisposedException ode)
			{
				throw new ObjectDisposedException(ode.ObjectName, ode.Message);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}
			finally
			{
				if (!message.Partial)
					RaiseMessageSubmitted(state, state.Close);
				message.Dispose();
			}
		}

		#endregion




	}
}
