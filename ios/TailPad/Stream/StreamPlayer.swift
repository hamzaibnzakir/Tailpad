import AVFoundation
import CoreMedia
import SwiftUI
import UIKit

/// Receives remote-play packets from the PC, rebuilds video frames, decodes them in hardware
/// (AVSampleBufferDisplayLayer → VideoToolbox) and plays the game's audio.
final class StreamPlayer {
    let layer = AVSampleBufferDisplayLayer()
    private let video = VideoDecoder()
    private let audio = AudioPlayer()
    private var assembler = FrameAssembler()
    private let lock = NSLock()

    /// frames shown in the last second, for the HUD
    private(set) var shownFrames = 0
    private(set) var dropped = 0

    init() {
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = UIColor.black.cgColor
    }

    func start() {
        lock.lock(); assembler = FrameAssembler(); lock.unlock()
        video.reset(layer)
        audio.start()
    }

    func stop() {
        audio.stop()
        DispatchQueue.main.async { self.layer.flushAndRemoveImage() }
    }

    /// Called from the network queue for every datagram the PC sends back.
    func handle(_ d: Data) {
        guard d.count >= 12 else { return }
        let magic = d.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }.littleEndian
        switch magic {
        case 0x3156_5054: // TPV1
            lock.lock()
            let frame = assembler.push(d)
            dropped = assembler.dropped
            lock.unlock()
            if let frame {
                if video.decode(frame.au, into: layer) {
                    shownFrames += 1
                } else {
                    lock.lock(); assembler.waitForKeyframe(); lock.unlock()
                }
            }
        case 0x3141_5054: // TPA1
            audio.play(opus: d.subdata(in: (d.startIndex + 12)..<d.endIndex))
        default: break
        }
    }

    func takeShownFrames() -> Int { let n = shownFrames; shownFrames = 0; return n }

    /// Screenshots/demo: play a recorded H.264 clip through the real decoder.
    func playDemo(_ annexB: Data) {
        let aus = FrameAssembler.splitAccessUnits(annexB)
        var i = 0
        Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] t in
            guard let self else { t.invalidate(); return }
            if i >= aus.count { i = 0; self.video.reset(self.layer) }
            _ = self.video.decode(aus[i], into: self.layer)
            i += 1
        }
    }
}

// MARK: - frame reassembly (same rules as the PC's FrameAssembler)

struct FrameAssembler {
    private struct Partial { var parts: [Data?]; var have = 0; var key: Bool }
    private var frames: [UInt32: Partial] = [:]
    private var last: UInt32 = 0
    private var any = false
    private var waitKey = true
    private(set) var dropped = 0

    mutating func waitForKeyframe() { waitKey = true }

    mutating func push(_ p: Data) -> (frame: UInt32, au: Data, key: Bool)? {
        guard p.count >= 20 else { return nil }
        func u32(_ o: Int) -> UInt32 { p.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: o, as: UInt32.self) }.littleEndian }
        func u16(_ o: Int) -> UInt16 { p.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: o, as: UInt16.self) }.littleEndian }
        let frame = u32(4), idx = Int(u16(8)), count = Int(u16(10))
        let key = (p[p.startIndex + 12] & 1) != 0
        if any && Int32(bitPattern: frame &- last) <= 0 { return nil }
        guard count > 0, idx < count else { return nil }
        var f = frames[frame] ?? Partial(parts: Array(repeating: nil, count: count), key: key)
        guard idx < f.parts.count, f.parts[idx] == nil else { return nil }
        f.parts[idx] = p.subdata(in: (p.startIndex + 20)..<p.endIndex)
        f.have += 1
        guard f.have == f.parts.count else { frames[frame] = f; return nil }

        frames[frame] = nil
        for k in frames.keys where Int32(bitPattern: k &- frame) < 0 { frames[k] = nil; dropped += 1; waitKey = true }
        if any && Int32(bitPattern: frame &- last) > 1 { waitKey = true }
        last = frame; any = true
        if frames.count > 120 { frames.removeAll() }
        if waitKey && !f.key { dropped += 1; return nil }
        waitKey = false
        var au = Data()
        for part in f.parts { au.append(part!) }
        return (frame, au, f.key)
    }

    /// Split a recorded Annex-B stream at access-unit delimiters (demo clip).
    static func splitAccessUnits(_ d: Data) -> [Data] {
        let b = [UInt8](d)
        var starts: [Int] = []
        var i = 0
        while i + 4 < b.count {
            if b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && (b[i + 3] & 0x1F) == 9 {
                starts.append(i > 0 && b[i - 1] == 0 ? i - 1 : i); i += 4
            } else { i += 1 }
        }
        starts.append(b.count)
        return zip(starts, starts.dropFirst()).map { Data(b[$0..<$1]) }
    }
}

// MARK: - H.264 → AVSampleBufferDisplayLayer

final class VideoDecoder {
    private var format: CMVideoFormatDescription?
    private var sps: Data?
    private var pps: Data?

    func reset(_ layer: AVSampleBufferDisplayLayer) {
        format = nil; sps = nil; pps = nil
        DispatchQueue.main.async { layer.flush() }
    }

    /// Returns false when the frame can't be shown (missing SPS/PPS or the decoder failed) → wait for a keyframe.
    func decode(_ au: Data, into layer: AVSampleBufferDisplayLayer) -> Bool {
        var payload = Data()
        var newParams = false
        for nal in Self.nals(au) {
            guard let first = nal.first else { continue }
            switch first & 0x1F {
            case 7: if nal != sps { sps = nal; newParams = true }
            case 8: if nal != pps { pps = nal; newParams = true }
            case 9: continue // access unit delimiter
            default:
                var len = UInt32(nal.count).bigEndian
                withUnsafeBytes(of: &len) { payload.append(contentsOf: $0) }
                payload.append(nal)
            }
        }
        if newParams || format == nil, let sps, let pps { format = Self.makeFormat(sps: sps, pps: pps) }
        guard let format, !payload.isEmpty else { return false }

        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: payload.count,
                                                 blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
                                                 dataLength: payload.count, flags: kCMBlockBufferAssureMemoryNowFlag,
                                                 blockBufferOut: &block) == noErr, let block else { return false }
        let copied = payload.withUnsafeBytes { raw -> OSStatus in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: payload.count)
        }
        guard copied == noErr else { return false }
        var sample: CMSampleBuffer?
        var size = payload.count
        guard CMSampleBufferCreateReady(allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
                                        sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
                                        sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample) == noErr,
              let sample else { return false }
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true), CFArrayGetCount(arr) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(arr, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        if layer.status == .failed {
            layer.flush()
            return false
        }
        layer.enqueue(sample)
        return true
    }

    private static func makeFormat(sps: Data, pps: Data) -> CMVideoFormatDescription? {
        var fmt: CMFormatDescription?
        let status: OSStatus = sps.withUnsafeBytes { s in
            pps.withUnsafeBytes { p in
                let ptrs: [UnsafePointer<UInt8>] = [s.bindMemory(to: UInt8.self).baseAddress!, p.bindMemory(to: UInt8.self).baseAddress!]
                let sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: kCFAllocatorDefault, parameterSetCount: 2,
                                                                          parameterSetPointers: ptrs, parameterSetSizes: sizes,
                                                                          nalUnitHeaderLength: 4, formatDescriptionOut: &fmt)
            }
        }
        return status == noErr ? fmt : nil
    }

    /// NAL units (without start codes) in an Annex-B buffer.
    static func nals(_ d: Data) -> [Data] {
        let b = [UInt8](d)
        var out: [Data] = []
        var start = -1
        var i = 0
        while i + 2 < b.count {
            if b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 {
                if start >= 0 {
                    var end = i
                    while end > start && b[end - 1] == 0 { end -= 1 } // trailing zeros / 4-byte start code
                    out.append(Data(b[start..<end]))
                }
                i += 3
                start = i
            } else { i += 1 }
        }
        if start >= 0 && start < b.count { out.append(Data(b[start..<b.count])) }
        return out
    }
}

// MARK: - Opus audio

final class AudioPlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private var converter: AVAudioConverter?
    private var inFormat: AVAudioFormat?
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 2, interleaved: false)!
    private let queue = DispatchQueue(label: "tailpad.audio")
    private var queued = 0
    private var running = false

    func start() {
        queue.async {
            guard !self.running else { return }
            do {
                let s = AVAudioSession.sharedInstance()
                try s.setCategory(.playback, mode: .default, options: [])
                try s.setPreferredIOBufferDuration(0.005)
                try s.setActive(true)
                var asbd = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatOpus, mFormatFlags: 0,
                                                       mBytesPerPacket: 0, mFramesPerPacket: 480, mBytesPerFrame: 0,
                                                       mChannelsPerFrame: 2, mBitsPerChannel: 0, mReserved: 0)
                guard let inF = AVAudioFormat(streamDescription: &asbd) else { return }
                self.inFormat = inF
                self.converter = AVAudioConverter(from: inF, to: self.outFormat)
                if self.node.engine == nil { self.engine.attach(self.node) }
                self.engine.connect(self.node, to: self.engine.mainMixerNode, format: self.outFormat)
                try self.engine.start()
                self.node.play()
                self.running = true
            } catch {
                self.running = false
            }
        }
    }

    func stop() {
        queue.async {
            self.node.stop()
            self.engine.stop()
            self.running = false
            self.queued = 0
        }
    }

    func play(opus: Data) {
        queue.async {
            guard self.running, let conv = self.converter, let inF = self.inFormat, !opus.isEmpty else { return }
            // keep latency low: if we're more than ~60 ms behind, skip ahead
            if self.queued > 6 { return }
            let comp = AVAudioCompressedBuffer(format: inF, packetCapacity: 1, maximumPacketSize: opus.count)
            opus.copyBytes(to: comp.data.assumingMemoryBound(to: UInt8.self), count: opus.count)
            comp.byteLength = UInt32(opus.count)
            comp.packetCount = 1
            comp.packetDescriptions?.pointee = AudioStreamPacketDescription(mStartOffset: 0, mVariableFramesInPacket: 0,
                                                                            mDataByteSize: UInt32(opus.count))
            guard let out = AVAudioPCMBuffer(pcmFormat: self.outFormat, frameCapacity: 960) else { return }
            var fed = false
            var err: NSError?
            let status = conv.convert(to: out, error: &err) { _, st in
                if fed { st.pointee = .noDataNow; return nil }
                fed = true
                st.pointee = .haveData
                return comp
            }
            guard status != .error, out.frameLength > 0 else { return }
            self.queued += 1
            self.node.scheduleBuffer(out) { [weak self] in self?.queue.async { self?.queued -= 1 } }
        }
    }
}

// MARK: - SwiftUI view

struct StreamVideoView: UIViewRepresentable {
    let player: StreamPlayer

    func makeUIView(context: Context) -> LayerView {
        let v = LayerView()
        v.backgroundColor = .black
        v.layer.addSublayer(player.layer)
        v.videoLayer = player.layer
        return v
    }

    func updateUIView(_ v: LayerView, context: Context) {}

    final class LayerView: UIView {
        var videoLayer: CALayer?
        override func layoutSubviews() {
            super.layoutSubviews()
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            videoLayer?.frame = bounds
            CATransaction.commit()
        }
    }
}
