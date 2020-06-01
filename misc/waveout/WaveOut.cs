using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WaveOut
{
    internal unsafe class WaveOut
    {
        #region P/Invoke

        private enum WaveFormatTag : ushort
        {
            Invalid = 0,
            Pcm = 1,
            Adpcm = 2,
            Float = 3,
            ALaw = 6,
            MuLaw = 7,
        }

        [StructLayout( LayoutKind.Sequential )]
        private struct WAVEFORMATEX
        {
            public WaveFormatTag wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        private enum WaveOutMessage
        {
            WOM_OPEN = 0x03BB,
            WOM_CLOSE = 0x03BC,
            WOM_DONE = 0x03BD
        }

        private delegate void WaveOutProc( IntPtr phwo, WaveOutMessage uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2 );

        [Flags]
        private enum WaveOpenFlags : uint
        {
            CALLBACK_NULL = 0x00000000,
            CALLBACK_WINDOW = 0x00010000,
            CALLBACK_THREAD = 0x00020000,
            CALLBACK_TASK = 0x00020000,
            CALLBACK_FUNCTION = 0x00030000,
            WAVE_FORMAT_QUERY = 0x00000001,
            WAVE_ALLOWSYNC = 0x00000002,
            WAVE_MAPPED = 0x00000004,
            WAVE_FORMAT_DIRECT = 0x00000008
        }

        private const uint WAVE_MAPPER = unchecked((uint)-1);

        [DllImport( "winmm.dll", SetLastError = true )]
        private static extern uint waveOutOpen( out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, WaveOutProc dwCallback, IntPtr dwInstance, WaveOpenFlags fdwOpen );

        [DllImport( "winmm.dll", SetLastError = true )]
        private static extern uint waveOutClose( IntPtr hwo );

        [Flags]
        private enum WaveHeaderFlags : uint
        {
            None = 0,
            BeginLoop = 4,
            Done = 1,
            EndLoop = 8,
            InQueue = 16,
            Prepared = 2
        }

        [StructLayout( LayoutKind.Sequential )]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public WaveHeaderFlags dwFlags;
            public uint dwLoops;

            public IntPtr lpNext;
            public IntPtr reserved;
        }


        [DllImport( "winmm.dll", SetLastError = true, CharSet = CharSet.Auto )]
        private static extern uint waveOutPrepareHeader( IntPtr hwo, WAVEHDR* pwh, uint cbwh );

        [DllImport( "winmm.dll", SetLastError = true, CharSet = CharSet.Auto )]
        private static extern uint waveOutWrite( IntPtr hwo, WAVEHDR* pwh, uint cbwh );

        #endregion

        private class Buffer
        {
            public readonly WAVEHDR* Header;
            public readonly short* Data;
            public readonly int Length;

            public Buffer( int length )
            {
                this.Data = (short*)Marshal.AllocHGlobal( sizeof( short ) * length );
                this.Length = length;

                this.Header = (WAVEHDR*)Marshal.AllocHGlobal( sizeof( WAVEHDR ) );
                this.Header->lpData = (IntPtr)this.Data;
                this.Header->dwBufferLength = this.Length * sizeof( short );
                this.Header->dwBytesRecorded = 0;
                this.Header->dwUser = IntPtr.Zero;
                this.Header->dwFlags = WaveHeaderFlags.None;
                this.Header->dwLoops = 0;
                this.Header->lpNext = IntPtr.Zero;
                this.Header->reserved = IntPtr.Zero;
            }

            // TODO: implement IDisposable and free the allocated memory
        }

        private static BlockingCollection<Buffer> s_FreeBuffers = new BlockingCollection<Buffer>( new ConcurrentQueue<Buffer>() ); // NOTE: this dude allocates a bit when used, uncool
        private static Queue<Buffer> s_PendingBuffers = new Queue<Buffer>();
        
        private static void Main( string[] args )
        {
            // open device
            var fmt = new WAVEFORMATEX();
            fmt.wFormatTag = WaveFormatTag.Pcm;
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = 44100;            
            fmt.nAvgBytesPerSec = fmt.nChannels * fmt.nSamplesPerSec * sizeof( short );
            fmt.nBlockAlign = (ushort)( fmt.nChannels * sizeof( short ) );
            fmt.wBitsPerSample = 16;
            fmt.cbSize = 0;

            Check( waveOutOpen( out IntPtr hwo, WAVE_MAPPER, ref fmt, Callback, IntPtr.Zero, WaveOpenFlags.CALLBACK_FUNCTION | WaveOpenFlags.WAVE_FORMAT_DIRECT ) );

            // let's go with four 0.25 second buffers
            for( int i = 0; i < 4; i++ )
            {
                var buf = new Buffer( 44100 / 4 );
                Check( waveOutPrepareHeader( hwo, buf.Header, (uint)sizeof( WAVEHDR ) ) );
                
                s_FreeBuffers.Add( buf );
            }                       

            // generate some data
            long offset = 0;
            while( true ) // might wanna add some exit condition
            {
                var buf = s_FreeBuffers.Take();

                for( int i = 0; i < buf.Length; i++ )
                {
                    // calculate time elapsed since start
                    long pos = offset + i;
                    double time = pos / (double)fmt.nSamplesPerSec;

                    buf.Data[i] = (short)( 5000 * Math.Sin( time * 2.0 * Math.PI * 1200 ) + // 1200 Hz
                                           5000 * Math.Sin( time * 2.0 * Math.PI * 1210 ) + // 1210 Hz
                                           4000 * Math.Sin( time * 2.0 * Math.PI * 210 ) ); // 210 Hz
                }

                offset += buf.Length;

                // write buffer
                Console.WriteLine( "Writing buffer" );
                lock( s_PendingBuffers )
                    s_PendingBuffers.Enqueue( buf );
                                
                Check( waveOutWrite( hwo, buf.Header, (uint)sizeof( WAVEHDR ) ) );                
            }

            // don't forget to call waveOutUnprepareHeader on the buffers when done

            // and shutdown
            Check( waveOutClose( hwo ) );
        }

        private static void Callback( IntPtr phwo, WaveOutMessage uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2 )
        {            
            if( uMsg == WaveOutMessage.WOM_DONE )
                lock( s_PendingBuffers )
                {
                    Console.WriteLine( "Got buffer back" );
                    var buf = s_PendingBuffers.Dequeue();
                    s_FreeBuffers.Add( buf );
                }
        }

        private static void Check( uint retVal )
        {
            if( retVal != 0 )
                throw new Exception( $"WaveOut function failed with error {retVal}." );
        }
    }
}
