using System.Runtime.InteropServices;
using DirectN;

namespace iPhoneCam.Net;

/// <summary>
/// ?單??汗??H.264 閫?Ⅳ?剁?AnnexB in ??RGB32(Bgr32) out??/// ??CMSH264DecoderMFT嚗V12嚗? CColorConvertDMO嚗GB32嚗遙雿?甇亙仃?? null嚗??詻?/// DirectN ?冽??扳?銝虜 VCamNetSampleSource嚗rameGenerator嚗?SetSize ?∪??喳潦?/// SetBlob(Guid,byte[],int)??雿? wrapper嚗ddBuffer嚗rocessInput ? .Object??/// WithLock 蝪賢???(IntPtr scan, uint maxLen, uint curLen)??/// ?瑁?蝺???Configure ???潔?嚗?憟賣???嚗?Decode ?翰?找?撠?MFT ?嚗?/// 頛詨 sample 銝??駁??橘?MFT ?航??甇交???? Release ??AV ?嚗?/// </summary>
public sealed class H264Preview : IDisposable
{
    private ComObject<IMFTransform>? _decoder;
    private ComObject<IMFTransform>? _converter;
    private readonly object _cfgLock = new();
    private readonly Queue<IDisposable> _retain = new();
    private int _w, _h, _fps = 30;
    private byte[] _spsRaw = [], _ppsRaw = [];
    private long _frameIndex;
    private bool _disposed;

    public bool IsConfigured { get { lock (_cfgLock) return _decoder != null; } }
    public string LastError { get; private set; } = "";
    public int OkCount { get; private set; }

    /// <summary>頛詨 sample 銝??駁??橘???敺嗾??銝?/summary>
    private void Retain(IDisposable d, int max = 4)
    {
        _retain.Enqueue(d);
        while (_retain.Count > max)
        {
            try { _retain.Dequeue().Dispose(); } catch { }
        }
    }

    public bool Configure(int width, int height, int fps, byte[] sps, byte[] pps)
    {
        if (width <= 0 || height <= 0 || sps.Length == 0 || pps.Length == 0) return false;
        ComObject<IMFTransform>? dec = null, conv = null;
        try
        {
            dec = new ComObject<IMFTransform>(
                (IMFTransform)System.Activator.CreateInstance(
                    Type.GetTypeFromCLSID(MFConstants.CLSID_CMSH264DecoderMFT)!)!);

            Functions.MFCreateMediaType(out var inType).ThrowOnError();
            inType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            inType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_H264).ThrowOnError();
            inType.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            try
            {
                byte[] seq = AnnexB.SpsPpsToAnnexB(sps, pps);
                inType.SetBlob(MFConstants.MF_MT_MPEG_SEQUENCE_HEADER, seq, seq.Length).ThrowOnError();
                dec.Object.SetInputType(0, inType, 0).ThrowOnError();
            }
            catch
            {
                // 摨??凋??停鋆訾?嚗DR ?撣?SPS/PPS嚗?                dec.Object.SetInputType(0, inType, 0).ThrowOnError();
            }

            Functions.MFCreateMediaType(out var outType).ThrowOnError();
            outType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            outType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            outType.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            dec.Object.SetOutputType(0, outType, 0).ThrowOnError();
            dec.Object.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero).ThrowOnError();

            conv = new ComObject<IMFTransform>(
                (IMFTransform)System.Activator.CreateInstance(
                    Type.GetTypeFromCLSID(MFConstants.CLSID_CColorConvertDMO)!)!);

            Functions.MFCreateMediaType(out var cIn).ThrowOnError();
            cIn.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            cIn.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            cIn.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            conv.Object.SetInputType(0, cIn, 0).ThrowOnError();

            Functions.MFCreateMediaType(out var cOut).ThrowOnError();
            cOut.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            cOut.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
            cOut.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            conv.Object.SetOutputType(0, cOut, 0).ThrowOnError();

            // ???潔?嚗?憟賣???嚗圾蝣澆銵?瘞賊??踹摰??撠?            lock (_cfgLock)
            {
                DisposeTransformsLocked();
                _decoder = dec; dec = null;
                _converter = conv; conv = null;
                _w = width; _h = height; _fps = fps > 0 ? fps : 30;
                _spsRaw = (byte[])sps.Clone(); _ppsRaw = (byte[])pps.Clone();
                _frameIndex = 0;
            }
            return true;
        }
        catch
        {
            try { dec?.Dispose(); } catch { }
            try { conv?.Dispose(); } catch { }
            return false;
        }
    }

    /// <summary>閫??閮?嚗VCC ??AnnexB ?嚗???Bgr32 雿?蝯??∟撓?箏? null??/summary>
    public byte[]? Decode(byte[] data)
    {
        ComObject<IMFTransform>? dec, conv;
        int w, h, fps;
        byte[] sps, pps;
        lock (_cfgLock)
        {
            dec = _decoder; conv = _converter;
            w = _w; h = _h; fps = _fps;
            sps = _spsRaw; pps = _ppsRaw;
        }
        if (dec == null || conv == null) return null;
        try
        {
            byte[]? annexb = AnnexB.ConvertAvcc(data);
            annexb ??= IsAnnexB(data) ? data : null;
            if (annexb == null) { LastError = $"AVCC 閫??憭望? len={data.Length}"; return null; }
            if (ContainsIdr(annexb))
            {
                byte[] hdr = AnnexB.SpsPpsToAnnexB(sps, pps);
                byte[] full = new byte[hdr.Length + annexb.Length];
                Buffer.BlockCopy(hdr, 0, full, 0, hdr.Length);
                Buffer.BlockCopy(annexb, 0, full, hdr.Length, annexb.Length);
                annexb = full;
            }
            var inSample = MFFunctions.MFCreateSample();
            var inBuf = MFFunctions.MFCreateMemoryBuffer((uint)annexb.Length);
            inBuf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                Marshal.Copy(annexb, 0, scan, (int)Math.Min(annexb.Length, maxLen)));
            inBuf.SetCurrentLength((uint)annexb.Length);
            inSample.AddBuffer(inBuf);
            Retain(inSample, 48); Retain(inBuf, 48);
            long t = _frameIndex * 10_000_000L / fps;
            IMFSample rawSample = inSample.Object;
            rawSample.SetSampleTime(t).ThrowOnError();
            rawSample.SetSampleDuration(10_000_000L / fps).ThrowOnError();
            try
            {
                dec.Object.ProcessInput(0, inSample.Object, 0).ThrowOnError();
            }
            catch
            {
                DrainNv12(dec, w, h);
                dec.Object.ProcessInput(0, inSample.Object, 0).ThrowOnError();
            }

            byte[]? nv12 = DrainNv12(dec, w, h);
            if (nv12 == null) { if (LastError == "") LastError = "?∟撓?綽?蝑?IDR ?圾蝣澆蝺抵?銝哨?"; return null; }
            try
            {
                var cInSample = MFFunctions.MFCreateSample();
                var cInBuf = MFFunctions.MFCreateMemoryBuffer((uint)nv12.Length);
                cInBuf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                    Marshal.Copy(nv12, 0, scan, (int)Math.Min(nv12.Length, maxLen)));
                cInBuf.SetCurrentLength((uint)nv12.Length);
                cInSample.AddBuffer(cInBuf);
                Retain(cInSample, 48); Retain(cInBuf, 48);
                conv.Object.ProcessInput(0, cInSample.Object, 0).ThrowOnError();
                using var rgbSample = MFFunctions.MFCreateSample();
                using var rgbBuf = MFFunctions.MFCreateMemoryBuffer((uint)(w * h * 4));
                rgbSample.AddBuffer(rgbBuf);
                HRESULT chr = HRESULTS.S_OK;
                rgbSample.WithComPointer(ptr =>
                {
                    var rgbBufs = new _MFT_OUTPUT_DATA_BUFFER[1];
                    rgbBufs[0].pSample = ptr;
                    chr = conv.Object.ProcessOutput(0, 1, rgbBufs, out _);
                });
                if (chr == HRESULTS.MF_E_TRANSFORM_NEED_MORE_INPUT) { LastError = "convert: need-more"; return null; }
                chr.ThrowOnError();
                using var buf = rgbSample.GetBufferByIndex(0);
                byte[] pixels = new byte[w * h * 4];
                buf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                    Marshal.Copy(scan, pixels, 0, (int)Math.Min(pixels.Length, maxLen)));
                _frameIndex++;
                return pixels;
            }
            catch (Exception e) { LastError = "convert: " + e.Message; return null; }
        }
        catch (Exception e) { LastError = "input: " + e.Message; return null; }
    }

    /// <summary>?芷?頧??剁?NV12?GB32嚗?蝯西???桃皜祈岫?具?/summary>
    public bool ConfigureRaw(int width, int height, int fps)
    {
        if (width <= 0 || height <= 0) return false;
        ComObject<IMFTransform>? conv = null;
        try
        {
            conv = new ComObject<IMFTransform>(
                (IMFTransform)System.Activator.CreateInstance(
                    Type.GetTypeFromCLSID(MFConstants.CLSID_CColorConvertDMO)!)!);

            Functions.MFCreateMediaType(out var cIn).ThrowOnError();
            cIn.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            cIn.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            cIn.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            conv.Object.SetInputType(0, cIn, 0).ThrowOnError();

            Functions.MFCreateMediaType(out var cOut).ThrowOnError();
            cOut.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            cOut.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
            cOut.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            conv.Object.SetOutputType(0, cOut, 0).ThrowOnError();

            lock (_cfgLock)
            {
                DisposeTransformsLocked();
                _converter = conv; conv = null;
                _w = width; _h = height; _fps = fps > 0 ? fps : 30;
            }
            return true;
        }
        catch (Exception e) { LastError = "cfgraw: " + e.Message; try { conv?.Dispose(); } catch { } return false; }
    }

    /// <summary>?芾?頧??剁?NV12 bytes ??RGB32??/summary>
    public byte[]? ConvertOnly(byte[] nv12)
    {
        ComObject<IMFTransform>? conv;
        int w, h;
        lock (_cfgLock) { conv = _converter; w = _w; h = _h; }
        if (conv == null) return null;
        try
        {
            var cInSample = MFFunctions.MFCreateSample();
            var cInBuf = MFFunctions.MFCreateMemoryBuffer((uint)nv12.Length);
            cInBuf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                Marshal.Copy(nv12, 0, scan, (int)Math.Min(nv12.Length, maxLen)));
            cInBuf.SetCurrentLength((uint)nv12.Length);
            cInSample.AddBuffer(cInBuf);
            Retain(cInSample, 48); Retain(cInBuf, 48);
            conv.Object.ProcessInput(0, cInSample.Object, 0).ThrowOnError();
            using var rgbSample = MFFunctions.MFCreateSample();
            using var rgbBuf = MFFunctions.MFCreateMemoryBuffer((uint)(w * h * 4));
            rgbSample.AddBuffer(rgbBuf);
            HRESULT chr = HRESULTS.S_OK;
            rgbSample.WithComPointer(ptr =>
            {
                var rgbBufs = new _MFT_OUTPUT_DATA_BUFFER[1];
                rgbBufs[0].pSample = ptr;
                chr = conv.Object.ProcessOutput(0, 1, rgbBufs, out _);
            });
            if (chr == HRESULTS.MF_E_TRANSFORM_NEED_MORE_INPUT) { LastError = "convert: need-more"; return null; }
            chr.ThrowOnError();
            using var buf = rgbSample.GetBufferByIndex(0);
            byte[] pixels = new byte[w * h * 4];
            buf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                Marshal.Copy(scan, pixels, 0, (int)Math.Min(pixels.Length, maxLen)));
            return pixels;
        }
        catch (Exception e) { LastError = "conv: " + e.Message; return null; }
    }

    /// <summary>?芾?閫?Ⅳ?剁?銝???嚗???NV12 bytes?策閫?Ⅳ?典?冽葫閰衣??/summary>
    public byte[]? DecodeOnly(byte[] data)
    {
        ComObject<IMFTransform>? dec;
        int w, h, fps;
        byte[] sps, pps;
        lock (_cfgLock)
        {
            dec = _decoder; w = _w; h = _h; fps = _fps;
            sps = _spsRaw; pps = _ppsRaw;
        }
        if (dec == null) return null;
        try
        {
            byte[]? annexb = AnnexB.ConvertAvcc(data);
            annexb ??= IsAnnexB(data) ? data : null;
            if (annexb == null) return null;
            if (ContainsIdr(annexb))
            {
                byte[] hdr = AnnexB.SpsPpsToAnnexB(sps, pps);
                byte[] full = new byte[hdr.Length + annexb.Length];
                Buffer.BlockCopy(hdr, 0, full, 0, hdr.Length);
                Buffer.BlockCopy(annexb, 0, full, hdr.Length, annexb.Length);
                annexb = full;
            }
            var inSample = MFFunctions.MFCreateSample();
            var inBuf = MFFunctions.MFCreateMemoryBuffer((uint)annexb.Length);
            inBuf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                Marshal.Copy(annexb, 0, scan, (int)Math.Min(annexb.Length, maxLen)));
            inBuf.SetCurrentLength((uint)annexb.Length);
            inSample.AddBuffer(inBuf);
            Retain(inSample, 48); Retain(inBuf, 48);
            long t = _frameIndex * 10_000_000L / fps;
            IMFSample rawSample = inSample.Object;
            rawSample.SetSampleTime(t).ThrowOnError();
            rawSample.SetSampleDuration(10_000_000L / fps).ThrowOnError();
            try
            {
                dec.Object.ProcessInput(0, inSample.Object, 0).ThrowOnError();
            }
            catch
            {
                DrainNv12(dec, w, h);
                dec.Object.ProcessInput(0, inSample.Object, 0).ThrowOnError();
            }
            byte[]? nv12 = DrainNv12(dec, w, h);
            if (nv12 != null) _frameIndex++;
            return nv12;
        }
        catch (Exception e) { LastError = "deconly: " + e.Message; return null; }
    }

    private static bool IsAnnexB(byte[] data)
    {
        if (data.Length < 4) return false;
        if (data[0] == 0 && data[1] == 0 && data[2] == 1) return true;
        return data.Length > 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
    }

    private static bool ContainsIdr(byte[] annexb)
    {
        for (int i = 0; i + 4 < annexb.Length; i++)
        {
            int sc = annexb[i] == 0 && annexb[i + 1] == 0
                ? (annexb[i + 2] == 1 ? 3 : (annexb[i + 2] == 0 && annexb[i + 3] == 1 ? 4 : 0)) : 0;
            if (sc > 0 && i + sc < annexb.Length && (annexb[i + sc] & 0x1F) == 5) return true;
        }
        return false;
    }

    /// <summary>??摰圾蝣澆鋆∟???賢??箔?嚗??單?敺?撘?NV12嚗??null嚗撓??sample ?芸???/summary>
    private byte[]? DrainNv12(ComObject<IMFTransform> dec, int w, int h)
    {
        byte[]? last = null;
        try
        {
            while (true)
            {
                using var outSample = MFFunctions.MFCreateSample();
                using var outBuf = MFFunctions.MFCreateMemoryBuffer((uint)(w * h * 3 / 2));
                outSample.AddBuffer(outBuf);
                HRESULT hr = HRESULTS.S_OK;
                outSample.WithComPointer(ptr =>
                {
                    var outBufs = new _MFT_OUTPUT_DATA_BUFFER[1];
                    outBufs[0].pSample = ptr;
                    hr = dec.Object.ProcessOutput(0, 1, outBufs, out _);
                });
                if (hr == HRESULTS.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                hr.ThrowOnError();
                using var buf = outSample.GetBufferByIndex(0);
                byte[] nv = new byte[w * h * 3 / 2];
                buf.WithLock((IntPtr scan, uint maxLen, uint _) =>
                    Marshal.Copy(scan, nv, 0, (int)Math.Min(nv.Length, maxLen)));
                last = nv;
            }
        }
        catch (Exception e) when (e.HResult == (int)HRESULTS.MF_E_TRANSFORM_NEED_MORE_INPUT) { }
        catch (Exception e) { LastError = "drain: " + e.Message; }
        return last;
    }

    private void DisposeTransformsLocked()
    {
        try { _decoder?.Object.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        try { _converter?.Dispose(); } catch { }
        _converter = null;
    }

    private void DisposeTransforms()
    {
        lock (_cfgLock) DisposeTransformsLocked();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTransforms();
        while (_retain.Count > 0)
        {
            try { _retain.Dequeue().Dispose(); } catch { }
        }
    }
}
