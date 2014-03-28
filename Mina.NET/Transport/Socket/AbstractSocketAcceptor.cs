﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Mina.Core.Future;
using Mina.Core.Service;
using Mina.Core.Session;
using Mina.Util;

namespace Mina.Transport.Socket
{
    public abstract class AbstractSocketAcceptor : AbstractIoAcceptor, ISocketAcceptor
    {
        private readonly AsyncSocketProcessor _processor;
        private Int32 _backlog;
        private Int32 _maxConnections;
        private Semaphore _connectionPool;
#if NET20
        private readonly WaitCallback _startAccept;
#else
        private readonly Action<Object> _startAccept;
#endif
        private Boolean _disposed;
        private readonly Dictionary<EndPoint, System.Net.Sockets.Socket> _listenSockets = new Dictionary<EndPoint, System.Net.Sockets.Socket>();

        protected AbstractSocketAcceptor()
            : this(1024)
        { }

        protected AbstractSocketAcceptor(Int32 maxConnections)
            : base(new DefaultSocketSessionConfig())
        {
            _maxConnections = maxConnections;
            _processor = new AsyncSocketProcessor(() => ManagedSessions.Values);
            this.SessionDestroyed += OnSessionDestroyed;
            _startAccept = StartAccept0;
            ReuseBuffer = true;
        }

        public Boolean ReuseAddress { get; set; }

        /// <summary>
        /// Gets or sets the backlog.
        /// </summary>
        public Int32 Backlog
        {
            get { return _backlog; }
            set { _backlog = value; }
        }

        /// <summary>
        /// Gets or sets the number of max connections.
        /// </summary>
        public Int32 MaxConnections
        {
            get { return _maxConnections; }
            set { _maxConnections = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to reuse the read buffer
        /// sent to <see cref="SocketSession.FilterChain"/> by
        /// <see cref="Core.Filterchain.IoFilterChain.FireMessageReceived(Object)"/>.
        /// </summary>
        /// <remarks>
        /// If any thread model, i.e. an <see cref="Filter.Executor.ExecutorFilter"/>,
        /// is added before filters that process the incoming <see cref="Core.Buffer.IoBuffer"/>
        /// in <see cref="Core.Filterchain.IoFilter.MessageReceived(Core.Filterchain.INextFilter, IoSession, Object)"/>,
        /// this must be set to <code>false</code> to avoid undetermined state
        /// of the read buffer. The default value is <code>true</code>.
        /// </remarks>
        public Boolean ReuseBuffer { get; set; }

        /// <inheritdoc/>
        protected override IEnumerable<EndPoint> BindInternal(IEnumerable<EndPoint> localEndPoints)
        {
            Dictionary<EndPoint, System.Net.Sockets.Socket> newListeners = new Dictionary<EndPoint, System.Net.Sockets.Socket>();
            try
            {
                // Process all the addresses
                foreach (EndPoint localEP in localEndPoints)
                {
                    EndPoint ep = localEP;
                    if (ep == null)
                        ep = new IPEndPoint(IPAddress.Any, 0);
                    System.Net.Sockets.Socket listenSocket = new System.Net.Sockets.Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    listenSocket.Bind(ep);
                    listenSocket.Listen(Backlog);
                    newListeners[listenSocket.LocalEndPoint] = listenSocket;
                }
            }
            catch (Exception)
            {
                // Roll back if failed to bind all addresses
                foreach (System.Net.Sockets.Socket listenSocket in newListeners.Values)
                {
                    try
                    {
                        listenSocket.Close();
                    }
                    catch (Exception ex)
                    {
                        ExceptionMonitor.Instance.ExceptionCaught(ex);
                    }
                }

                throw;
            }

            if (MaxConnections > 0)
                _connectionPool = new Semaphore(MaxConnections, MaxConnections);

            foreach (KeyValuePair<EndPoint, System.Net.Sockets.Socket> pair in newListeners)
            {
                _listenSockets[pair.Key] = pair.Value;
                StartAccept(new ListenerContext(pair.Value));
            }

            _processor.IdleStatusChecker.Start();

            return newListeners.Keys;
        }

        /// <inheritdoc/>
        protected override void UnbindInternal(IEnumerable<EndPoint> localEndPoints)
        {
            foreach (EndPoint ep in localEndPoints)
            {
                System.Net.Sockets.Socket listenSocket;
                if (!_listenSockets.TryGetValue(ep, out listenSocket))
                    continue;
                listenSocket.Close();
                _listenSockets.Remove(ep);
            }

            if (_listenSockets.Count == 0)
            {
                _processor.IdleStatusChecker.Stop();

                if (_connectionPool != null)
                {
                    _connectionPool.Close();
                    _connectionPool = null;
                }
            }
        }

        private void StartAccept(ListenerContext listener)
        {
            if (_connectionPool == null)
            {
                BeginAccept(listener);
            }
            else
            {
#if NET20
                System.Threading.ThreadPool.QueueUserWorkItem(_startAccept, listener);
#else
                System.Threading.Tasks.Task.Factory.StartNew(_startAccept, listener);
#endif
            }
        }

        private void StartAccept0(Object state)
        {
            _connectionPool.WaitOne();
            BeginAccept((ListenerContext)state);
        }

        private void OnSessionDestroyed(Object sender, IoSessionEventArgs e)
        {
            if (_connectionPool != null)
                _connectionPool.Release();
        }

        /// <inheritdoc/>
        protected abstract IoSession NewSession(IoProcessor<SocketSession> processor, System.Net.Sockets.Socket socket);

        /// <summary>
        /// Begins an accept operation.
        /// </summary>
        /// <param name="listener"></param>
        protected abstract void BeginAccept(ListenerContext listener);

        /// <summary>
        /// Ends an accept operation.
        /// </summary>
        /// <param name="socket">the accepted client socket</param>
        /// <param name="listener">the <see cref="ListenerContext"/></param>
        protected void EndAccept(System.Net.Sockets.Socket socket, ListenerContext listener)
        {
            if (socket != null)
            {
                IoSession session = NewSession(_processor, socket);
                try
                {
                    InitSession<IoFuture>(session, null, null);
                    session.Processor.Add(session);
                }
                catch (Exception ex)
                {
                    ExceptionMonitor.Instance.ExceptionCaught(ex);
                }
            }

            // Accept the next connection request
            StartAccept(listener);
        }

        /// <inheritdoc/>
        protected override void Dispose(Boolean disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_listenSockets.Count > 0)
                    {
                        foreach (System.Net.Sockets.Socket listenSocket in _listenSockets.Values)
                        {
                            ((IDisposable)listenSocket).Dispose();
                        }
                    }
                    if (_connectionPool != null)
                    {
                        ((IDisposable)_connectionPool).Dispose();
                        _connectionPool = null;
                    }
                    _processor.Dispose();
                    base.Dispose(disposing);
                    _disposed = true;
                }
            }
        }

        protected class ListenerContext
        {
            private readonly System.Net.Sockets.Socket _socket;

            public ListenerContext(System.Net.Sockets.Socket socket)
            {
                _socket = socket;
            }

            public System.Net.Sockets.Socket Socket
            {
                get { return _socket; }
            }

            public Object Tag { get; set; }
        }
    }
}