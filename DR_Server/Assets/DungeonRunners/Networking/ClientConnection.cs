using System;
using System.Collections;
using System.Net.Sockets;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Represents a client connection with Unity-compatible async operations
    /// </summary>
    public class ClientConnection
    {
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public bool IsConnected => Client?.Connected ?? false;
        public string RemoteEndPoint { get; private set; }

        private byte[] _receiveBuffer = new byte[8192];
        private bool _isReading;

        public event Action<byte[]> OnDataReceived;
        public event Action OnDisconnected;

        public ClientConnection(TcpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        }

        public void StartReceiving(MonoBehaviour coroutineRunner)
        {
            if (_isReading) return;
            _isReading = true;
            coroutineRunner.StartCoroutine(ReceiveLoop());
        }

        private IEnumerator ReceiveLoop()
        {
            while (_isReading && IsConnected)
            {
                // Read on background thread
                bool dataAvailable = false;
                byte[] receivedData = null;

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (Stream.DataAvailable)
                        {
                            int bytesRead = Stream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
                            if (bytesRead > 0)
                            {
                                receivedData = new byte[bytesRead];
                                Array.Copy(_receiveBuffer, receivedData, bytesRead);
                                dataAvailable = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error reading from client: {ex}");
                        _isReading = false;
                    }
                });

                // Wait a frame
                yield return null;

                // Process received data on main thread
                if (dataAvailable && receivedData != null)
                {
                    OnDataReceived?.Invoke(receivedData);
                }

                // Small delay to prevent tight loop
                yield return new WaitForSeconds(0.01f);
            }

            // Connection closed
            OnDisconnected?.Invoke();
        }

        public void Send(byte[] data)
        {
            if (!IsConnected || data == null || data.Length == 0)
                return;

            try
            {
                Stream.Write(data, 0, data.Length);
                Stream.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error sending data: {ex}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _isReading = false;
            
            try
            {
                Stream?.Close();
                Client?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error disconnecting: {ex}");
            }
        }
    }
}