using CryptoBase.Abstractions;
using CryptoBase.Digests;
using CryptoBase.Macs.Hmac;
using Shadowsocks.Controller;
using Shadowsocks.Encryption;
using Shadowsocks.Enums;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Shadowsocks.Obfs
{
    class AuthChain_a : VerifySimpleBase
    {
        protected class AuthDataAesChain : AuthData
        {
        }

        public AuthChain_a(string method)
                : base(method)
        {
            has_sent_header = false;
            has_recv_header = false;
            pack_id = 1;
            recv_id = 1;
            SALT = method;
            var bytes = new byte[4];
            g_random.GetBytes(bytes);
            random = new Random(BitConverter.ToInt32(bytes, 0));
        }

        private static Dictionary<string, int[]> _obfs = new()
        {
            { "auth_chain_a", new[] { 1, 0, 1 } }
        };

        protected bool has_sent_header;
        protected bool has_recv_header;
        protected static RNGCryptoServiceProvider g_random = new();
        protected string SALT;

        protected uint pack_id;
        protected uint recv_id;
        protected byte[] user_key;
        protected byte[] user_id;
        protected byte[] send_buffer;
        protected int last_datalength;
        protected byte[] last_client_hash;
        protected byte[] last_server_hash;
        protected xorshift128plus random_client = new();
        protected xorshift128plus random_server = new();
        protected IEncryptor encryptor;

        protected const int overhead = 4;

        public static List<string> SupportedObfs()
        {
            return new(_obfs.Keys);
        }

        public override Dictionary<string, int[]> GetObfs()
        {
            return _obfs;
        }

        protected override void Disposing()
        {
            if (encryptor != null)
            {
                encryptor.Dispose();
                encryptor = null;
            }
        }

        public override object InitData()
        {
            return new AuthDataAesChain();
        }

        public override bool isKeepAlive()
        {
            return true;
        }

        public override bool isAlwaysSendback()
        {
            return true;
        }

        public override int GetOverhead()
        {
            return overhead;
        }

        protected IMac CreateHMAC(byte[] key)
        {
            return HmacUtils.Create(DigestType.Md5, key);
        }

        protected virtual int GetRandLen(int datalength, xorshift128plus rd, byte[] last_hash)
        {
            if (datalength > 1440)
            {
                return 0;
            }

            rd.init_from_bin(last_hash, datalength);
            if (datalength > 1300)
            {
                return (int)(rd.next() % 31);
            }

            if (datalength > 900)
            {
                return (int)(rd.next() % 127);
            }

            if (datalength > 400)
            {
                return (int)(rd.next() % 521);
            }

            return (int)(rd.next() % 1021);
        }

        protected int UdpGetRandLen(xorshift128plus rd, byte[] last_hash)
        {
            rd.init_from_bin(last_hash);
            return (int)(rd.next() % 127);
        }

        protected int GetRandStartPos(int rand_len, xorshift128plus rd)
        {
            if (rand_len > 0)
            {
                return (int)(rd.next() % 8589934609 % (ulong)rand_len);
            }

            return 0;
        }

        protected int GetRandLen(int datalength)
        {
            return GetRandLen(datalength, random_client, last_client_hash);
        }

        public void PackData(byte[] data, int datalength, byte[] outdata, out int outlength)
        {
            var rand_len = GetRandLen(datalength);
            outlength = rand_len + datalength + 2;
            outdata[0] = (byte)(datalength ^ last_client_hash[14]);
            outdata[1] = (byte)((datalength >> 8) ^ last_client_hash[15]);
            {
                var rnd_data = new byte[rand_len];
                random.NextBytes(rnd_data);
                encryptor.Encrypt(data, datalength, data, out datalength);
                if (datalength > 0)
                {
                    if (rand_len > 0)
                    {
                        var start_pos = GetRandStartPos(rand_len, random_client);
                        Array.Copy(data, 0, outdata, 2 + start_pos, datalength);
                        Array.Copy(rnd_data, 0, outdata, 2, start_pos);
                        Array.Copy(rnd_data, start_pos, outdata, 2 + start_pos + datalength, rand_len - start_pos);
                    }
                    else
                    {
                        Array.Copy(data, 0, outdata, 2, datalength);
                    }
                }
                else
                {
                    rnd_data.CopyTo(outdata, 2);
                }
            }

            var key = new byte[user_key.Length + 4];
            user_key.CopyTo(key, 0);
            BitConverter.GetBytes(pack_id).CopyTo(key, key.Length - 4);

            using var md5 = CreateHMAC(key);
            ++pack_id;
            {
                md5.Update(outdata.AsSpan(0, outlength));
                var md5data = new byte[md5.Length];
                md5.GetMac(md5data);
                last_client_hash = md5data;
                md5data[..2].CopyTo(outdata.AsSpan(outlength, 2));
                outlength += 2;
            }
        }

        public virtual void OnInitAuthData(ulong unixTimestamp)
        {

        }

        public void PackAuthData(byte[] data, int datalength, byte[] outdata, out int outlength)
        {
            const int authhead_len = 4 + 8 + 4 + 16 + 4;
            var encrypt = new byte[24];

            if (Server.data is AuthDataAesChain authData)
            {
                lock (authData)
                {
                    if (authData.connectionID > 0xFF000000)
                    {
                        authData.clientID = null;
                    }

                    if (authData.clientID == null)
                    {
                        authData.clientID = new byte[4];
                        g_random.GetBytes(authData.clientID);
                        authData.connectionID = (uint)BitConverter.ToInt32(authData.clientID, 0) % 0xFFFFFD;
                    }

                    authData.connectionID += 1;
                    Array.Copy(authData.clientID, 0, encrypt, 4, 4);
                    Array.Copy(BitConverter.GetBytes(authData.connectionID), 0, encrypt, 8, 4);
                }
            }

            outlength = authhead_len;
            var encrypt_data = new byte[32];
            var key = new byte[Server.Iv.Length + Server.key.Length];
            Server.Iv.CopyTo(key, 0);
            Server.key.CopyTo(key, Server.Iv.Length);

            var utc_time_second = (ulong)Math.Floor(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds);
            var utc_time = (uint)utc_time_second;
            Array.Copy(BitConverter.GetBytes(utc_time), 0, encrypt, 0, 4);
            OnInitAuthData(utc_time_second);

            encrypt[12] = (byte)Server.overhead;
            encrypt[13] = (byte)(Server.overhead >> 8);

            // first 12 bytes
            {
                Span<byte> rnd = stackalloc byte[4];
                RandomNumberGenerator.Fill(rnd);
                rnd.CopyTo(outdata);
                using var md5 = CreateHMAC(key);
                md5.Update(rnd);
                var md5data = new byte[md5.Length];
                md5.GetMac(md5data);
                last_client_hash = md5data;
                md5data[..8].CopyTo(outdata.AsSpan(rnd.Length, 8));
            }
            // uid & 16 bytes auth data
            {
                var uid = new byte[4];
                var index_of_split = Server.param.IndexOf(':');
                if (index_of_split > 0)
                {
                    try
                    {
                        var user = uint.Parse(Server.param.Substring(0, index_of_split));
                        user_key = System.Text.Encoding.UTF8.GetBytes(Server.param.Substring(index_of_split + 1));
                        BitConverter.GetBytes(user).CopyTo(uid, 0);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log(LogLevel.Warn, $"Faild to parse auth param, fallback to basic mode. {ex}");
                    }
                }
                if (user_key == null)
                {
                    random.NextBytes(uid);
                    user_key = Server.key;
                }
                for (var i = 0; i < 4; ++i)
                {
                    uid[i] ^= last_client_hash[8 + i];
                }

                var encrypt_key = user_key;

                CryptoUtils.SsAes128(Convert.ToBase64String(encrypt_key) + SALT, encrypt.AsSpan(0, 16), encrypt_data.AsSpan(0, 16));
                Array.Copy(encrypt_data, 0, encrypt, 4, 16);
                uid.CopyTo(encrypt, 0);
            }
            // final HMAC
            {
                using var md5 = CreateHMAC(user_key);
                md5.Update(encrypt.AsSpan(0, 20));
                var md5data = new byte[md5.Length];
                md5.GetMac(md5data);
                last_server_hash = md5data;
                md5data[..4].CopyTo(encrypt.AsSpan(20, 4));
            }
            encrypt.CopyTo(outdata, 12);
            encryptor = EncryptorFactory.GetEncryptor("rc4", Convert.ToBase64String(user_key) + Convert.ToBase64String(last_client_hash, 0, 16));

            // combine first chunk
            {
                var pack_outdata = new byte[outdata.Length];
                PackData(data, datalength, pack_outdata, out var pack_outlength);
                Array.Copy(pack_outdata, 0, outdata, outlength, pack_outlength);
                outlength += pack_outlength;
            }
        }

        // plaindata == null    try send buffer data, return null if empty buffer
        // datalength == 0      sendback, return 0
        // datalength == -1     keepalive
        public override byte[] ClientPreEncrypt(byte[] plaindata, int datalength, out int outlength)
        {
            var outdata = new byte[datalength + datalength / 10 + 32];
            var packdata = new byte[9000];
            var data = plaindata == null ? send_buffer : plaindata;
            outlength = 0;
            if (data == null)
            {
                return null;
            }

            if (data == send_buffer)
            {
                datalength = send_buffer.Length;
                send_buffer = null;
            }
            else if (send_buffer != null)
            {
                if (datalength <= 0)
                {
                    return outdata;
                }

                Array.Resize(ref send_buffer, send_buffer.Length + datalength);
                Array.Copy(data, 0, send_buffer, send_buffer.Length - datalength, datalength);
                data = send_buffer;
                datalength = send_buffer.Length;
                send_buffer = null;
            }
            var unit_len = Server.tcp_mss - Server.overhead;
            var ogn_datalength = datalength;
            if (!has_sent_header)
            {
                var _datalength = Math.Min(1200, datalength);
                PackAuthData(data, _datalength, packdata, out var outlen);
                has_sent_header = true;
                Util.Utils.SetArrayMinSize2(ref outdata, outlength + outlen);
                Array.Copy(packdata, 0, outdata, outlength, outlen);
                outlength += outlen;
                datalength -= _datalength;
                var newdata = new byte[datalength];
                Array.Copy(data, _datalength, newdata, 0, newdata.Length);
                data = newdata;

                send_buffer = data.Length > 0 ? data : null;

                return outdata;
            }

            if (datalength > 120 * 4 && pack_id < 32)
            {
                var send_len = LinearRandomInt(120 * 16);
                if (send_len < datalength)
                {
                    send_len = TrapezoidRandomInt(Math.Min(datalength - 1, Server.tcp_mss - overhead) - 1, -0.3) + 1;  // must less than datalength

                    send_len = datalength - send_len;

                    if (send_len > 0)
                    {
                        send_buffer = new byte[send_len];
                        Array.Copy(data, datalength - send_len, send_buffer, 0, send_len);
                        datalength -= send_len;
                    }
                }
            }
            while (datalength > unit_len)
            {
                PackData(data, unit_len, packdata, out var outlen);
                Util.Utils.SetArrayMinSize2(ref outdata, outlength + outlen);
                Array.Copy(packdata, 0, outdata, outlength, outlen);
                outlength += outlen;
                datalength -= unit_len;
                var newdata = new byte[datalength];
                Array.Copy(data, unit_len, newdata, 0, newdata.Length);
                data = newdata;
            }
            if (datalength > 0 || ogn_datalength == -1)
            {
                if (ogn_datalength == -1)
                {
                    datalength = 0;
                }

                PackData(data, datalength, packdata, out var outlen);
                Util.Utils.SetArrayMinSize2(ref outdata, outlength + outlen);
                Array.Copy(packdata, 0, outdata, outlength, outlen);
                outlength += outlen;
            }
            last_datalength = ogn_datalength;
            return outdata;
        }

        public override byte[] ClientPostDecrypt(byte[] plaindata, int datalength, out int outlength)
        {
            var outdata = new byte[recv_buf_len + datalength];
            Array.Copy(plaindata, 0, recv_buf, recv_buf_len, datalength);
            recv_buf_len += datalength;
            outlength = 0;
            var key = new byte[user_key.Length + 4];
            user_key.CopyTo(key, 0);
            while (recv_buf_len > 4)
            {
                BitConverter.GetBytes(recv_id).CopyTo(key, key.Length - 4);
                using var md5 = CreateHMAC(key);

                var data_len = ((recv_buf[1] ^ last_server_hash[15]) << 8) + (recv_buf[0] ^ last_server_hash[14]);
                var rand_len = GetRandLen(data_len, random_server, last_server_hash);
                var len = rand_len + data_len;
                if (len >= 4096)
                {
                    throw new ObfsException("ClientPostDecrypt data error");
                }
                if (len + 4 > recv_buf_len)
                {
                    break;
                }

                md5.Update(recv_buf.AsSpan(0, len + 2));
                var md5data = new byte[md5.Length];
                md5.GetMac(md5data);
                if (md5data[0] != recv_buf[len + 2]
                    || md5data[1] != recv_buf[len + 3]
                )
                {
                    throw new ObfsException("ClientPostDecrypt data uncorrect checksum");
                }

                {
                    int pos;
                    if (data_len > 0 && rand_len > 0)
                    {
                        pos = 2 + GetRandStartPos(rand_len, random_server);
                    }
                    else
                    {
                        pos = 2;
                    }
                    var outlen = data_len;
                    Util.Utils.SetArrayMinSize2(ref outdata, outlength + outlen);
                    var data = new byte[outlen];
                    Array.Copy(recv_buf, pos, data, 0, outlen);
                    encryptor.Decrypt(data, outlen, data, out outlen);
                    last_server_hash = md5data;
                    if (recv_id == 1)
                    {
                        Server.tcp_mss = data[0] | (data[1] << 8);
                        pos = 2;
                        outlen -= 2;
                    }
                    else
                    {
                        pos = 0;
                    }
                    Array.Copy(data, pos, outdata, outlength, outlen);
                    outlength += outlen;
                    recv_buf_len -= len + 4;
                    Array.Copy(recv_buf, len + 4, recv_buf, 0, recv_buf_len);
                    ++recv_id;
                }
            }
            return outdata;
        }

        public override byte[] ClientUdpPreEncrypt(byte[] plaindata, int datalength, out int outlength)
        {
            var outdata = new byte[datalength + 1024];
            if (user_key == null)
            {
                user_id = new byte[4];
                var index_of_split = Server.param.IndexOf(':');
                if (index_of_split > 0)
                {
                    try
                    {
                        var user = uint.Parse(Server.param.Substring(0, index_of_split));
                        user_key = System.Text.Encoding.UTF8.GetBytes(Server.param.Substring(index_of_split + 1));
                        BitConverter.GetBytes(user).CopyTo(user_id, 0);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log(LogLevel.Warn, $"Faild to parse auth param, fallback to basic mode. {ex}");
                    }
                }
                if (user_key == null)
                {
                    random.NextBytes(user_id);
                    user_key = Server.key;
                }
            }
            var auth_data = new byte[3];
            random.NextBytes(auth_data);

            using var md5 = CreateHMAC(Server.key);
            md5.Update(auth_data);
            var md5data = new byte[md5.Length];
            md5.GetMac(md5data);
            var rand_len = UdpGetRandLen(random_client, md5data);
            var rand_data = new byte[rand_len];
            random.NextBytes(rand_data);
            outlength = datalength + rand_len + 8;
            encryptor = EncryptorFactory.GetEncryptor("rc4", Convert.ToBase64String(user_key) + Convert.ToBase64String(md5data, 0, 16));
            encryptor.Encrypt(plaindata, datalength, outdata, out datalength);
            rand_data.CopyTo(outdata, datalength);
            auth_data.CopyTo(outdata, outlength - 8);
            var uid = new byte[4];
            for (var i = 0; i < 4; ++i)
            {
                uid[i] = (byte)(user_id[i] ^ md5data[i]);
            }
            uid.CopyTo(outdata, outlength - 5);
            {
                using var userMd5 = CreateHMAC(user_key);
                userMd5.Update(outdata.AsSpan(0, outlength - 1));
                Span<byte> span = stackalloc byte[userMd5.Length];
                userMd5.GetMac(span);
                outdata[outlength - 1] = span[0];
            }
            return outdata;
        }

        public override byte[] ClientUdpPostDecrypt(byte[] plaindata, int datalength, out int outlength)
        {
            if (datalength <= 8)
            {
                outlength = 0;
                return plaindata;
            }

            using var md5 = CreateHMAC(user_key);
            md5.Update(plaindata.AsSpan(0, datalength - 1));
            var md5data = new byte[md5.Length];
            md5.GetMac(md5data);
            if (md5data[0] != plaindata[datalength - 1])
            {
                outlength = 0;
                return plaindata;
            }

            using var serverMd5 = CreateHMAC(Server.key);
            serverMd5.Update(plaindata.AsSpan(datalength - 8, 7));
            serverMd5.GetMac(md5data);
            var rand_len = UdpGetRandLen(random_server, md5data);
            outlength = datalength - rand_len - 8;
            encryptor = EncryptorFactory.GetEncryptor("rc4", Convert.ToBase64String(user_key) + Convert.ToBase64String(md5data, 0, 16));
            encryptor.Decrypt(plaindata, outlength, plaindata, out outlength);
            return plaindata;
        }
    }
}
