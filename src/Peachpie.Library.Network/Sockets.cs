﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using Pchp.Core;
using Pchp.Core.Resources;
using Pchp.Library.Streams;

namespace Peachpie.Library.Network
{
    /// <summary>
    /// PHP socket resource.
    /// </summary>
    class SocketResource : PhpResource
    {
        public Socket Socket { get; }

        public SocketResource(Socket socket) : base("socket")
        {
            this.Socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        protected override void FreeManaged()
        {
            this.Socket.Close();
        }

        public static SocketResource GetValid(PhpResource resource)
        {
            if (resource is SocketResource s && s.IsValid)
            {
                return s;
            }
            else
            {
                PhpException.Throw(PhpError.Warning, ErrResources.invalid_socket_resource);
                return null;
            }
        }

        /// <summary>
        /// Gets or sets last error caused by the operation on this socket.
        /// </summary>
        public SocketError LastError { get; set; } = SocketError.Success;
    }

    #region Helpers

    /// <summary>
    /// Helper socket methods.
    /// </summary>
    static class SocketsExtension
    {
        public static AddressFamily GetAddressFamily(this Sockets.PhpAddressFamily af) => af switch
        {
            Sockets.PhpAddressFamily.UNIX => AddressFamily.Unix,
            Sockets.PhpAddressFamily.INET => AddressFamily.InterNetwork,
            Sockets.PhpAddressFamily.INET6 => AddressFamily.InterNetworkV6,
            _ => default,
        };

        public static SocketType GetSocketType(this Sockets.PhpSocketType type) => type switch
        {
            Sockets.PhpSocketType.STREAM => SocketType.Stream,
            Sockets.PhpSocketType.DGRAM => SocketType.Dgram,
            Sockets.PhpSocketType.RAW => SocketType.Raw,
            Sockets.PhpSocketType.SEQPACKET => SocketType.Seqpacket,
            Sockets.PhpSocketType.RDM => SocketType.Rdm,
            _ => default,
        };
    }

    #endregion

    /// <summary>
    /// "socket" extension functions.
    /// </summary>
    [PhpExtension("sockets")]
    public static class Sockets
    {
        #region Constants

        public const int AF_UNIX = 1;
        public const int AF_INET = 2;
        public const int AF_INET6 = 23;

        /// <summary>
        /// Socket family to be used for creating new sockets.
        /// </summary>
        public enum PhpAddressFamily
        {
            UNIX = AF_UNIX,
            INET = AF_INET,
            INET6 = AF_INET6,
        }

        public const int SOCK_STREAM = (int)SocketType.Stream; // 1
        public const int SOCK_DGRAM = (int)SocketType.Dgram; // 2
        public const int SOCK_RAW = (int)SocketType.Raw; // 3
        public const int SOCK_RDM = (int)SocketType.Rdm; // 4
        public const int SOCK_SEQPACKET = (int)SocketType.Seqpacket; // 5

        /// <summary>
        /// Socket underlying communication type.
        /// </summary>
        public enum PhpSocketType
        {
            STREAM = SOCK_STREAM,
            DGRAM = SOCK_DGRAM,
            RAW = SOCK_RAW,
            SEQPACKET = SOCK_SEQPACKET,
            RDM = SOCK_RDM,
        }

        public const int SOL_SOCKET = (int)SocketOptionLevel.Socket; // 65535
        public const int SOL_TCP = (int)SocketOptionLevel.Tcp; // 6
        public const int SOL_UDP = (int)SocketOptionLevel.Udp; // 17

        public const int SO_FREE = 8;
        public const int SO_NOSERVER = 16;
        public const int SO_DEBUG = (int)SocketOptionName.Debug; // 1
        public const int SO_REUSEADDR = (int)SocketOptionName.ReuseAddress; // 4
        public const int SO_KEEPALIVE = (int)SocketOptionName.KeepAlive; // 8
        public const int SO_DONTROUTE = (int)SocketOptionName.DontRoute; // 16
        public const int SO_LINGER = (int)SocketOptionName.Linger; // 128
        public const int SO_BROADCAST = (int)SocketOptionName.Broadcast; // 32
        public const int SO_OOBINLINE = (int)SocketOptionName.OutOfBandInline; // 256
        public const int SO_SNDBUF = (int)SocketOptionName.SendBuffer;
        public const int SO_RCVBUF = (int)SocketOptionName.ReceiveBuffer;
        public const int SO_SNDLOWAT = (int)SocketOptionName.SendLowWater;
        public const int SO_RCVLOWAT = (int)SocketOptionName.ReceiveLowWater;
        public const int SO_SNDTIMEO = (int)SocketOptionName.SendTimeout;
        public const int SO_RCVTIMEO = (int)SocketOptionName.ReceiveTimeout;
        public const int SO_TYPE = (int)SocketOptionName.Type;
        public const int SO_ERROR = (int)SocketOptionName.Error;
        public const int TCP_NODELAY = (int)SocketOptionName.NoDelay;

        /// <summary>
        /// Reading stops at \n or \r.
        /// </summary>
        public const int PHP_NORMAL_READ = 1;

        /// <summary>
        /// (Default) Safe for reading binary data.
        /// </summary>
        public const int PHP_BINARY_READ = 2;

        #endregion

        static void HandleException(Context ctx, SocketResource resource, Exception ex)
        {
            PhpException.Throw(PhpError.Warning, ex.Message);

            // remember last error

            if (resource != null)
            {
                resource.LastError = ex is SocketException se ? se.SocketErrorCode : SocketError.SocketError;
            }

            if (ctx != null && ex is SocketException sex)
            {
                ctx.SetProperty(sex);
            }
        }

        static EndPoint BindEndPoint(AddressFamily af, string address, int port = 0)
        {
            switch (af)
            {
                case AddressFamily.InterNetwork:
                case AddressFamily.InterNetworkV6:
                    // address is IP address
                    // port is used
                    if (IPAddress.TryParse(address, out var ipaddress))
                    {
                        return new IPEndPoint(ipaddress, port);
                    }

                    // TODO: warning

                    return null;

                default:
                    PhpException.ArgumentValueNotSupported(nameof(AddressFamily), af);
                    return null;
            }
        }

        /// <summary>
        /// Accepts a connection on a socket.
        /// </summary>
        /// <returns>Returns a new socket resource on success, or FALSE on error.</returns>
        [return: CastToFalse]
        public static PhpResource socket_accept(PhpResource socket)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return null;// FALSE
            }

            if (s.Socket.Connected)
            {
                throw new InvalidOperationException("socket connected");
                //return null; // FALSE
            }

            try
            {
                return new SocketResource(s.Socket.Accept());
            }
            catch (SocketException ex)
            {
                HandleException(null, s, ex);
                return null;
            }
        }

        //socket_addrinfo_bind — Create and bind to a socket from a given addrinfo
        //socket_addrinfo_connect — Create and connect to a socket from a given addrinfo
        //socket_addrinfo_explain — Get information about addrinfo
        //socket_addrinfo_lookup — Get array with contents of getaddrinfo about the given hostname

        /// <summary>
        /// Binds a name to a socket.
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="address"></param>
        /// <param name="port">The port parameter is only used when binding an <see cref="AF_INET"/> socket, and designates the port on which to listen for connections.</param>
        /// <returns>Returns TRUE on success or FALSE on failure.</returns>
        public static bool socket_bind(PhpResource socket, string address, int port = 0)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            var endpoint = BindEndPoint(s.Socket.AddressFamily, address, port);
            if (endpoint != null)
            {
                try
                {
                    s.Socket.Bind(endpoint);
                    return true;
                }
                catch (SocketException ex)
                {
                    HandleException(null, s, ex);
                }
            }

            //
            return false;
        }

        /// <summary>
        /// Clears the error on the socket or the last error code.
        /// </summary>
        public static void socket_clear_error(Context ctx, PhpResource socket = null)
        {
            if (socket != null)
            {
                var s = SocketResource.GetValid(socket);
                if (s != null)
                {
                    // get error on the socket resource
                    s.LastError = SocketError.Success;
                }
            }
            else
            {            // get last SocketException from context
                ctx.SetProperty<SocketException>(null);
            }
        }

        /// <summary>
        /// Closes a socket resource.
        /// </summary>
        public static void socket_close(PhpResource socket)
        {
            SocketResource.GetValid(socket)?.Dispose();
        }

        //socket_cmsg_space — Calculate message buffer size

        /// <summary>
        /// Initiates a connection on a socket.
        /// </summary>
        public static bool socket_connect(PhpResource socket, string address, int port = 0)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            // NOT: address cannot be a host name, otherwise we would resolve it here

            var endpoint = BindEndPoint(s.Socket.AddressFamily, address, port);
            if (endpoint != null)
            {
                // TODO: If the socket is non-blocking then return FALSE with an error Operation now in progress.

                try
                {
                    s.Socket.Connect(endpoint);
                    return true;
                }
                catch (SocketException ex)
                {
                    HandleException(null, s, ex);
                }
            }

            return false;
        }

        //socket_create_listen — Opens a socket on port to accept connections
        //socket_create_pair — Creates a pair of indistinguishable sockets and stores them in an array

        /// <summary>
        /// Create a socket(endpoint for communication).
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="type"></param>
        /// <param name="protocol"></param>
        /// <returns></returns>
        public static PhpResource socket_create(PhpAddressFamily domain, PhpSocketType type, ProtocolType protocol)
        {
            // TODO: validate arguments and return FALSE
            return new SocketResource(new Socket(domain.GetAddressFamily(), type.GetSocketType(), protocol));
        }

        //socket_export_stream — Export a socket extension resource into a stream that encapsulates a socket

        /// <summary>
        /// Gets socket options for the socket
        /// </summary>
        public static PhpValue socket_get_option(PhpResource socket, SocketOptionLevel level, SocketOptionName option, PhpValue option_value)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            switch (option)
            {
                case SocketOptionName.Linger:
                    {
                        var linger = s.Socket.LingerState;
                        return new PhpArray(2)
                        {
                            { "l_onoff", linger.Enabled ? 1 : 0 },
                            { "l_linger", linger.LingerTime },
                        };
                    }

                case SocketOptionName.ReceiveTimeout:
                    return new PhpArray(2)
                    {
                        { "sec",  s.Socket.ReceiveTimeout / 1000 },
                        { "usec", (s.Socket.ReceiveTimeout % 1000) * 1000 }
                    };

                case SocketOptionName.SendTimeout:
                    return new PhpArray(2)
                    {
                        { "sec",  s.Socket.SendTimeout / 1000 },
                        { "usec", (s.Socket.SendTimeout % 1000) * 1000 }
                    };

                //case SocketOptionName.NODELAY:
                //    return s.Socket.NoDelay ? 1 : 0;


                case SocketOptionName.Type:
                    Debug.Assert((int)SocketType.Stream == SOCK_STREAM);
                    Debug.Assert((int)SocketType.Dgram == SOCK_DGRAM);
                    // SocketType enum corresponds to SOCK_ constants
                    return (int)s.Socket.SocketType;

                case SocketOptionName.Error:
                case SocketOptionName.Debug:
                case SocketOptionName.ReuseAddress:
                case SocketOptionName.KeepAlive:
                case SocketOptionName.DontRoute:
                case SocketOptionName.Broadcast:
                case SocketOptionName.OutOfBandInline:
                case SocketOptionName.SendBuffer:
                case SocketOptionName.ReceiveBuffer:
                case SocketOptionName.SendLowWater:
                case SocketOptionName.ReceiveLowWater:
                default:
                    try
                    {
                        var value = s.Socket.GetSocketOption(level, option);
                        if (value is int ivalue)
                        {
                            return ivalue;
                        }
                    }
                    catch (Exception ex)
                    {
                        HandleException(null, s, ex);
                        return false;
                    }
                    break;
            }

            //
            PhpException.ArgumentValueNotSupported(nameof(option), option);
            return false;
        }

        //socket_getopt — Alias of socket_get_option
        //socket_getpeername — Queries the remote side of the given socket which may either result in host/port or in a Unix filesystem path, dependent on its type

        /// <summary>
        /// Queries the local side of the given socket which may either result in host/port or in a Unix filesystem path, dependent on its type.
        /// </summary>
        public static bool socket_getsockname(PhpResource socket, out string addr) => socket_getsockname(socket, out addr, out _);

        /// <summary>
        /// Queries the local side of the given socket which may either result in host/port or in a Unix filesystem path, dependent on its type.
        /// </summary>
        public static bool socket_getsockname(PhpResource socket, out string addr, out int port)
        {
            addr = null;
            port = 0;

            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            var ep = s.Socket.LocalEndPoint;
            if (ep is IPEndPoint ipep)
            {
                addr = ipep.Address.ToString();
                port = ipep.Port;
                return true;
            }
            else
            {
                PhpException.ArgumentValueNotSupported(nameof(AddressFamily), s.Socket.AddressFamily);
                return false;
            }
        }


        //socket_import_stream — Import a stream

        /// <summary>
        /// Returns the last error on the socket.
        /// </summary>
        public static int socket_last_error(Context ctx, PhpResource socket = null)
        {
            if (socket != null)
            {
                var s = SocketResource.GetValid(socket);
                if (s != null)
                {
                    // get error on the socket resource
                    return (int)s.LastError;
                }
            }

            // get last SocketException from context
            var err = ctx.TryGetProperty<SocketException>();
            if (err != null)
            {
                return (int)err.SocketErrorCode;
            }

            //
            return (int)SocketError.Success;
        }


        /// <summary>
        /// Listens for a connection on a socket.
        /// </summary>
        public static bool socket_listen(PhpResource socket, int backlog = 0)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            // applicable only to sockets of type SOCK_STREAM or SOCK_SEQPACKET
            switch (s.Socket.SocketType)
            {
                case SocketType.Stream:
                case SocketType.Seqpacket:

                    try
                    {
                        s.Socket.Listen(backlog);
                        return true;
                    }
                    catch (SocketException ex)
                    {
                        HandleException(null, s, ex);
                        return false;
                    }

                default:
                    PhpException.ArgumentValueNotSupported(nameof(SocketType), s.Socket.SocketType);
                    return false;
            }
        }

        /// <summary>
        /// Reads a maximum of length bytes from a socket.
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="length"></param>
        /// <param name="type">Either <see cref="PHP_NORMAL_READ"/> or <see cref="PHP_BINARY_READ"/>.</param>
        /// <returns></returns>
        [return: CastToFalse]
        public static PhpString socket_read(PhpResource socket, int length, int type = PHP_BINARY_READ)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return default; // FALSE
            }

            if (type == PHP_BINARY_READ)
            {
                var pool = ArrayPool<byte>.Shared;
                var buffer = pool.Rent(length);
                try
                {
                    var received = s.Socket.Receive(buffer, length, SocketFlags.None);
                    if (received == 0)
                    {
                        return PhpString.Empty;
                    }

                    var result = new byte[received];
                    Array.Copy(buffer, result, received);

                    //
                    return new PhpString(result);
                }
                catch (SocketException ex)
                {
                    HandleException(null, s, ex);
                }
                finally
                {
                    pool.Return(buffer);
                }
            }
            else if (type == PHP_NORMAL_READ)
            {
                // TODO: PHP_NORMAL_READ
                throw new NotImplementedException();
            }

            //
            return default; // false;
        }

        //socket_recv — Receives data from a connected socket
        //socket_recvfrom — Receives data from a socket whether or not it is connection-oriented
        //socket_recvmsg — Read a message
        //socket_select — Runs the select() system call on the given arrays of sockets with a specified timeout
        //socket_send — Sends data to a connected socket
        //socket_sendmsg — Send a message
        //socket_sendto — Sends a message to a socket, whether it is connected or not
        //socket_set_block — Sets blocking mode on a socket resource
        //socket_set_nonblock — Sets nonblocking mode for file descriptor fd

        /// <summary>
        /// Sets socket options for the socket.
        /// </summary>
        public static bool socket_set_option(PhpResource socket, SocketOptionLevel level, SocketOptionName option, PhpValue option_value)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return false;
            }

            PhpArray arr;

            switch (option)
            {
                case SocketOptionName.Linger:
                    if (option_value.IsPhpArray(out arr))
                    {
                        s.Socket.LingerState = new LingerOption((int)arr["l_onoff"] != 0, (int)arr["l_linger"]);
                        return true;
                    }
                    return false;

                case SocketOptionName.SendTimeout:
                case SocketOptionName.ReceiveTimeout:
                    if (option_value.IsPhpArray(out arr))
                    {
                        var sec = (int)arr["sec"];
                        var msec = (int)arr["usec"] / 1000;

                        // in ms, 0 and -1 indicates infinite
                        var ms = (sec < 0) ? 0 : (sec * 1000 + msec);
                        if (option == SocketOptionName.ReceiveTimeout)
                        {
                            s.Socket.ReceiveTimeout = ms;
                        }
                        else if (option == SocketOptionName.SendTimeout)
                        {
                            s.Socket.SendTimeout = ms;
                        }
                        return true;
                    }
                    return false;

                // case SocketOptionName.ERROR: // cannot be set
                // case SocketOptionName.TYPE: // cannot be set

                case SocketOptionName.Debug:
                case SocketOptionName.ReuseAddress:
                case SocketOptionName.KeepAlive:
                case SocketOptionName.DontRoute:
                case SocketOptionName.Broadcast:
                case SocketOptionName.OutOfBandInline:
                case SocketOptionName.SendBuffer:
                case SocketOptionName.ReceiveBuffer:
                case SocketOptionName.SendLowWater:
                case SocketOptionName.ReceiveLowWater:
                default:
                    PhpException.ArgumentValueNotSupported(nameof(option), option);
                    return false;
            }
        }

        /// <summary>
        /// Alias of <see cref="socket_set_option"/>.
        /// </summary>
        public static bool socket_setopt(PhpResource socket, SocketOptionLevel level, SocketOptionName option, PhpValue option_value)
            => socket_set_option(socket, level, option, option_value);

        /// <summary>
        /// Shuts down a socket for receiving, sending, or both.
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="how">
        /// 0	Shutdown socket reading<br/>
        /// 1	Shutdown socket writing<br/>
        /// 2	Shutdown socket reading and writing<br/>
        /// </param>
        public static bool socket_shutdown(PhpResource socket, SocketShutdown how = SocketShutdown.Both)
        {
            Debug.Assert((int)SocketShutdown.Receive == 0);
            Debug.Assert((int)SocketShutdown.Send == 1);
            Debug.Assert((int)SocketShutdown.Both == 2);

            var s = SocketResource.GetValid(socket);
            if (s != null)
            {
                try
                {
                    s.Socket.Shutdown(how);
                    return true;
                }
                catch (SocketException ex)
                {
                    HandleException(null, s, ex);
                }
            }

            //
            return false;
        }

        //socket_strerror — Return a string describing a socket error
        public static string socket_strerror(SocketError errno)
        {
            // TODO: get full error message
            return errno.ToString();
        }

        /// <summary>
        /// Write to a socket.
        /// </summary>
        [return: CastToFalse]
        public static int socket_write(Context ctx, PhpResource socket, PhpString buffer, int length = -1)
        {
            var s = SocketResource.GetValid(socket);
            if (s == null)
            {
                return -1; // FALSE
            }

            var bytes = buffer.ToBytes(ctx);
            if (length < 0 || length > bytes.Length)
            {
                length = bytes.Length;
            }

            try
            {
                return s.Socket.Send(bytes, 0, length, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                HandleException(ctx, s, ex);
            }

            //
            return -1; // FALSE
        }

        //socket_wsaprotocol_info_export — Exports the WSAPROTOCOL_INFO Structure
        //socket_wsaprotocol_info_import — Imports a Socket from another Process
        //socket_wsaprotocol_info_release — Releases an exported WSAPROTOCOL_INFO Structure
    }
}
