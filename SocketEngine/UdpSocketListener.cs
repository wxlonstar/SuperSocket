﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Pool;

namespace SuperSocket.SocketEngine
{
    class UdpSocketListener : SocketListenerBase
    {
        private Socket m_ListenSocket;

        private IPool<SaeState> m_SaePool;

        private EndPoint m_AnyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        public UdpSocketListener(ListenerInfo info, IPool<SaeState> saePool)
            : base(info)
        {
            m_SaePool = saePool;
        }

        /// <summary>
        /// Starts to listen
        /// </summary>
        /// <param name="config">The server config.</param>
        /// <returns></returns>
        public override bool Start(IServerConfig config)
        {
            SaeState saeState = null;

            try
            {
                m_ListenSocket = new Socket(this.EndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                m_ListenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_ListenSocket.Bind(this.EndPoint);

                //Mono doesn't support it
                if (Platform.SupportSocketIOControlByCodeEnum)
                {
                    uint IOC_IN = 0x80000000;
                    uint IOC_VENDOR = 0x18000000;
                    uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                    byte[] optionInValue = { Convert.ToByte(false) };
                    byte[] optionOutValue = new byte[4];
                    m_ListenSocket.IOControl((int)SIO_UDP_CONNRESET, optionInValue, optionOutValue);
                }

                saeState = m_SaePool.Get();
                var sae = saeState.Sae;
                sae.UserToken = saeState;
                sae.RemoteEndPoint = m_AnyEndPoint;
                sae.Completed += new EventHandler<SocketAsyncEventArgs>(eventArgs_Completed);

                if (!m_ListenSocket.ReceiveFromAsync(sae))
                    eventArgs_Completed(this, sae);

                return true;
            }
            catch (Exception e)
            {
                if (saeState != null)
                {
                    saeState.Sae.Completed -= new EventHandler<SocketAsyncEventArgs>(eventArgs_Completed);
                    m_SaePool.Return(saeState);
                }
                
                OnError(e);
                return false;
            }
        }

        void eventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
            e.Completed -= new EventHandler<SocketAsyncEventArgs>(eventArgs_Completed);

            if (e.SocketError != SocketError.Success)
            {
                var errorCode = (int)e.SocketError;

                //The listen socket was closed
                if (errorCode == 995 || errorCode == 10004 || errorCode == 10038)
                    return;

                OnError(new SocketException(errorCode));
            }

            if (e.LastOperation == SocketAsyncOperation.ReceiveFrom)
            {
                try
                {
                    OnNewClientAccepted(m_ListenSocket, e);
                }
                catch (Exception exc)
                {
                    OnError(exc);
                    m_SaePool.Return(e.UserToken as SaeState);
                }

                SaeState newState = null;

                try
                {
                    newState = m_SaePool.Get();
                    var sae = newState.Sae;
                    sae.UserToken = newState;
                    sae.RemoteEndPoint = m_AnyEndPoint;
                    sae.Completed += new EventHandler<SocketAsyncEventArgs>(eventArgs_Completed);
                    
                    if(!m_ListenSocket.ReceiveFromAsync(sae))
                        eventArgs_Completed(this, sae);
                }
                catch (Exception exc)
                {
                    OnError(exc);

                    if (newState != null)
                    {
                        newState.Sae.Completed -= new EventHandler<SocketAsyncEventArgs>(eventArgs_Completed);
                        m_SaePool.Return(newState);
                    }
                }
            }
        }

        public override void Stop()
        {
            if (m_ListenSocket == null)
                return;

            lock(this)
            {
                if (m_ListenSocket == null)
                    return;

                if(!Platform.IsMono)
                {
                    try
                    {
                        m_ListenSocket.Shutdown(SocketShutdown.Both);
                    }
                    catch { }
                }

                try
                {
                    m_ListenSocket.Close();
                }
                catch { }
                finally
                {
                    m_ListenSocket = null;
                }
            }

            OnStopped();
        }
    }
}
