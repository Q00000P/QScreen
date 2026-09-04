import SwiftUI
import AppKit
import KeyboardShortcuts
import CoreGraphics
import UniformTypeIdentifiers
import Vision
import CoreImage
import AudioToolbox
import ServiceManagement
import AVFoundation
import ImageIO
import ScreenCaptureKit

// --- OTA Автообновление через GitHub ---
public enum UpdateChecker {
    public static let currentVersion = "10.1.0"
    public static let repo = "Q00000P/QScreen"

    public static func checkForUpdates(isUserInitiated: Bool = false) {
        guard let url = URL(string: "https://api.github.com/repos/\(repo)/releases/latest") else { return }

        var request = URLRequest(url: url)
        request.setValue("QScreen-Mac", forHTTPHeaderField: "User-Agent")

        URLSession.shared.dataTask(with: request) { data, _, error in
            guard let data = data, error == nil,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tagName = json["tag_name"] as? String else {
                if isUserInitiated {
                    DispatchQueue.main.async {
                        let alert = NSAlert()
                        alert.messageText = "Ошибка"
                        alert.informativeText = "Не удалось проверить обновления."
                        alert.runModal()
                    }
                }
                return
            }

            let remoteVer = tagName.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            if remoteVer.compare(currentVersion, options: .numeric) == .orderedDescending {
                // Только mac-ассет: в релизе лежит и Windows-zip
                var zipUrl: String?
                if let assets = json["assets"] as? [[String: Any]] {
                    for asset in assets {
                        if let name = asset["name"] as? String, name.lowercased().contains("macos"), name.hasSuffix(".zip"),
                           let dl = asset["browser_download_url"] as? String {
                            zipUrl = dl
                            break
                        }
                    }
                }
                // Тег новее, но mac-ассета нет — релиз не для этой платформы, не дёргаем
                guard let zipUrl = zipUrl else {
                    if isUserInitiated {
                        DispatchQueue.main.async {
                            let alert = NSAlert()
                            alert.messageText = "Обновлений нет"
                            alert.informativeText = "Релиз v\(remoteVer) не содержит сборку для macOS.\nТекущая версия: v\(currentVersion)."
                            alert.runModal()
                        }
                    }
                    return
                }

                DispatchQueue.main.async {
                    let alert = NSAlert()
                    alert.messageText = "Обновление QScreen"
                    alert.informativeText = "Доступна новая версия QScreen v\(remoteVer)!\nТекущая: v\(currentVersion)\n\nСкачать и установить обновление автоматически?"
                    alert.addButton(withTitle: "Обновить")
                    alert.addButton(withTitle: "Отмена")
                    if alert.runModal() == .alertFirstButtonReturn { performSilentUpdate(zipUrl: zipUrl) }
                }
            } else if isUserInitiated {
                DispatchQueue.main.async {
                    let alert = NSAlert()
                    alert.messageText = "Обновлений нет"
                    alert.informativeText = "У вас установлена актуальная версия QScreen (v\(currentVersion))."
                    alert.runModal()
                }
            }
        }.resume()
    }

    private static func performSilentUpdate(zipUrl: String) {
        guard let url = URL(string: zipUrl) else { return }
        let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent("QScreen_Update_\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        let zipPath = tempDir.appendingPathComponent("update.zip")

        URLSession.shared.downloadTask(with: url) { location, _, _ in
            guard let location = location else { return }
            try? FileManager.default.moveItem(at: location, to: zipPath)

            let extractDir = tempDir.appendingPathComponent("extracted")
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            process.arguments = ["-xk", zipPath.path, extractDir.path]
            try? process.run()
            process.waitUntilExit()

            let appBundlePath = Bundle.main.bundlePath
            let parentDir = URL(fileURLWithPath: appBundlePath).deletingLastPathComponent().path
            let script = """
            sleep 1
            cp -R "\(extractDir.path)/QScreen.app" "\(parentDir)"
            open "\(appBundlePath)"
            rm -rf "\(tempDir.path)"
            """

            let scriptPath = tempDir.appendingPathComponent("update.sh")
            try? script.write(to: scriptPath, atomically: true, encoding: .utf8)

            let shProcess = Process()
            shProcess.executableURL = URL(fileURLWithPath: "/bin/sh")
            shProcess.arguments = [scriptPath.path]
            try? shProcess.run()

            DispatchQueue.main.async {
                NSApp.terminate(nil)
            }
        }.resume()
    }
}

// --- Горячие клавиши ---
extension KeyboardShortcuts.Name {
    static let captureArea = Self("captureArea", default: .init(.four, modifiers: [.command, .shift]))
    static let captureSmart = Self("captureSmart", default: .init(.six, modifiers: [.command, .shift]))
    static let captureScroll = Self("captureScroll", default: .init(.seven, modifiers: [.command, .shift]))
    static let captureScreen = Self("captureScreen", default: .init(.three, modifiers: [.command, .shift]))
    static let recordArea = Self("recordArea", default: .init(.five, modifiers: [.command, .shift]))
    static let recordStop = Self("recordStop", default: .init(.s, modifiers: [.command, .option]))
    static let recordPause = Self("recordPause", default: .init(.p, modifiers: [.command, .option]))
}

func playShutterSound() {
    AudioServicesPlaySystemSound(1108)
}

// --- Координаты: AppKit (низ-лево, глобально) ↔ Quartz (верх-лево от primary-экрана) ---
// Считаем от NSScreen.screens[0] (primary, origin 0,0), а не от NSScreen.main — тот меняется с фокусом.
enum Coord {
    static var primary: NSScreen { NSScreen.screens.first ?? NSScreen.main! }
    static func toQuartz(_ ak: CGRect) -> CGRect { CGRect(x: ak.minX, y: primary.frame.maxY - ak.maxY, width: ak.width, height: ak.height) }
    static func toAppKit(_ q: CGRect) -> CGRect { CGRect(x: q.minX, y: primary.frame.maxY - q.maxY, width: q.width, height: q.height) }
    static func screen(containing p: CGPoint) -> NSScreen { NSScreen.screens.first { $0.frame.contains(p) } ?? primary }
    static func screen(for r: CGRect) -> NSScreen {
        var best = primary; var bestArea: CGFloat = -1
        for s in NSScreen.screens { let a = s.frame.intersection(r); let area = a.isNull ? 0 : a.width * a.height; if area > bestArea { bestArea = area; best = s } }
        return best
    }
}

extension NSImage {
    /// Пикселей на point у этой картинки (не у монитора)
    var pixelScale: CGFloat {
        guard let rep = representations.first, rep.pixelsWide > 0, size.width > 0 else { return NSScreen.main?.backingScaleFactor ?? 2.0 }
        return CGFloat(rep.pixelsWide) / size.width
    }
}

final class FilenameHelper {
    static func getDefaultFormat() -> String {
        return UserDefaults.standard.string(forKey: "defaultImageFormat") ?? "png"
    }

    static func generateFilename(ext: String? = nil) -> String {
        let prefix = UserDefaults.standard.string(forKey: "filenamePrefix") ?? "QScreen"
        let formatChoice = UserDefaults.standard.string(forKey: "filenameDateFormat") ?? "dd.MM.yyyy_HH.mm.ss"
        let chosenExt = ext ?? getDefaultFormat()

        let dateStr: String
        if formatChoice == "unix" {
            dateStr = "\(Int(Date().timeIntervalSince1970))"
        } else {
            let formatter = DateFormatter()
            formatter.dateFormat = formatChoice
            dateStr = formatter.string(from: Date())
        }

        let cleanPrefix = prefix.trimmingCharacters(in: .whitespacesAndNewlines)
        let baseName = cleanPrefix.isEmpty ? dateStr : "\(cleanPrefix)_\(dateStr)"
        return "\(baseName).\(chosenExt)"
    }

    static func getDefaultSaveFolder() -> URL {
        if let savedPath = UserDefaults.standard.string(forKey: "defaultSaveFolderPath"),
           !savedPath.isEmpty {
            let url = URL(fileURLWithPath: (savedPath as NSString).expandingTildeInPath)
            if FileManager.default.fileExists(atPath: url.path) {
                return url
            }
        }
        return FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first ?? URL(fileURLWithPath: NSHomeDirectory())
    }
}

// --- Экспорт форматов изображений ---
final class ImageExportHelper {
    static func exportData(image: NSImage, format: String? = nil, quality: Double? = nil) -> (Data, String)? {
        let fmt = (format ?? UserDefaults.standard.string(forKey: "defaultImageFormat") ?? "png").lowercased()
        let q = quality ?? (UserDefaults.standard.object(forKey: "jpegQuality") != nil ? UserDefaults.standard.double(forKey: "jpegQuality") : 0.85)

        if fmt == "heic" || fmt == "heif" {
            if let tiff = image.tiffRepresentation,
               let source = CGImageSourceCreateWithData(tiff as CFData, nil),
               let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) {
                let mutableData = NSMutableData()
                if let dest = CGImageDestinationCreateWithData(mutableData as CFMutableData, UTType.heic.identifier as CFString, 1, nil) {
                    let options: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: q]
                    CGImageDestinationAddImage(dest, cg, options as CFDictionary)
                    if CGImageDestinationFinalize(dest) {
                        return (mutableData as Data, "heic")
                    }
                }
            }
        }

        if fmt == "pdf" {
            if let tiff = image.tiffRepresentation,
               let bitmap = NSBitmapImageRep(data: tiff) {
                let pdfData = NSMutableData()
                var mediaBox = CGRect(origin: .zero, size: image.size)
                guard let consumer = CGDataConsumer(data: pdfData as CFMutableData),
                      let ctx = CGContext(consumer: consumer, mediaBox: &mediaBox, nil) else { return nil }
                ctx.beginPage(mediaBox: &mediaBox)
                if let cg = bitmap.cgImage { ctx.draw(cg, in: mediaBox) }
                ctx.endPage()
                ctx.closePDF()
                return (pdfData as Data, "pdf")
            }
        }

        guard let tiffData = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiffData) else { return nil }

        switch fmt {
        case "jpg", "jpeg":
            let props: [NSBitmapImageRep.PropertyKey: Any] = [.compressionFactor: q]
            if let data = bitmap.representation(using: .jpeg, properties: props) { return (data, "jpg") }
        default:
            if let data = bitmap.representation(using: .png, properties: [:]) { return (data, "png") }
        }
        return nil
    }
}

// --- Скролл-скриншоты ---
final class ScrollStitcher {
    static func stitch(frames: [NSImage]) -> NSImage? {
        guard !frames.isEmpty else { return nil }
        if frames.count == 1 { return frames[0] }

        var cgFrames: [CGImage] = []
        for f in frames {
            if let tiff = f.tiffRepresentation,
               let source = CGImageSourceCreateWithData(tiff as CFData, nil),
               let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) {
                cgFrames.append(cg)
            }
        }
        guard !cgFrames.isEmpty else { return nil }

        let width = cgFrames[0].width
        var stitchedCG = cgFrames[0]

        for i in 1..<cgFrames.count {
            let nextCG = cgFrames[i]
            let overlap = findVerticalOverlap(top: stitchedCG, bottom: nextCG)
            stitchedCG = combineImages(top: stitchedCG, bottom: nextCG, overlap: overlap, width: width)
        }

        let scale = frames[0].pixelScale
        let ptSize = NSSize(width: CGFloat(stitchedCG.width) / scale, height: CGFloat(stitchedCG.height) / scale)
        return NSImage(cgImage: stitchedCG, size: ptSize)
    }

    private static func findVerticalOverlap(top: CGImage, bottom: CGImage) -> Int {
        let maxSearch = min(top.height / 2, bottom.height / 2, 600)
        guard maxSearch > 20 else { return 0 }

        guard let topData = top.dataProvider?.data, let topPtr = CFDataGetBytePtr(topData),
              let botData = bottom.dataProvider?.data, let botPtr = CFDataGetBytePtr(botData) else { return 0 }

        let topBPR = top.bytesPerRow
        let botBPR = bottom.bytesPerRow
        let checkWidth = min(top.width, bottom.width)

        var bestOverlap = 0
        var minDiff = Int.max

        for overlap in stride(from: 20, to: maxSearch, by: 2) {
            var diff = 0
            let sampleRows = min(15, overlap)
            for r in 0..<sampleRows {
                let topY = top.height - overlap + r
                let botY = r
                let topRowOffset = topY * topBPR
                let botRowOffset = botY * botBPR

                for x in stride(from: 0, to: checkWidth, by: 4) {
                    let p1 = topPtr[topRowOffset + x * 4]
                    let p2 = botPtr[botRowOffset + x * 4]
                    diff += abs(Int(p1) - Int(p2))
                }
            }
            if diff < minDiff {
                minDiff = diff
                bestOverlap = overlap
            }
        }

        if minDiff < (checkWidth / 4) * 15 * 18 { return bestOverlap }
        return 0
    }

    private static func combineImages(top: CGImage, bottom: CGImage, overlap: Int, width: Int) -> CGImage {
        let newHeight = top.height + bottom.height - overlap
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(data: nil, width: width, height: newHeight, bitsPerComponent: 8, bytesPerRow: width * 4, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return top
        }

        let topRect = CGRect(x: 0, y: bottom.height - overlap, width: width, height: top.height)
        ctx.draw(top, in: topRect)

        let botRect = CGRect(x: 0, y: 0, width: width, height: bottom.height)
        ctx.draw(bottom, in: botRect)

        return ctx.makeImage() ?? top
    }
}

// --- Менеджер скролл-скриншотов ---
@MainActor
final class ScrollCaptureManager {
    static let shared = ScrollCaptureManager()
    private var panelWindow: NSWindow?
    private var targetQuartzRect: CGRect = .zero
    private var capturedFrames: [NSImage] = []
    private var onFinished: ((NSImage) -> Void)?

    var isActive: Bool { panelWindow != nil }

    func startScrollingSession(quartzRect: CGRect, onComplete: @escaping (NSImage) -> Void) {
        cancelSession()
        self.targetQuartzRect = quartzRect
        self.capturedFrames.removeAll()
        self.onFinished = onComplete

        captureCurrentFrame()
        showControlPanel()
    }

    func captureCurrentFrame() {
        if let img = CaptureEngine.shared.capture(quartzRect: targetQuartzRect) {
            capturedFrames.append(img)
            updatePanel()
        }
    }

    func finishSession() {
        closePanel()
        if let stitched = ScrollStitcher.stitch(frames: capturedFrames) {
            onFinished?(stitched)
        } else if let first = capturedFrames.first {
            onFinished?(first)
        }
    }

    func cancelSession() {
        closePanel()
        capturedFrames.removeAll()
    }

    private func showControlPanel() {
        closePanel()
        let targetAK = Coord.toAppKit(targetQuartzRect)
        let screen = Coord.screen(for: targetAK)

        let panelW: CGFloat = 280
        let panelH: CGFloat = 110
        let posX = min(screen.visibleFrame.maxX - panelW - 20, max(screen.visibleFrame.minX + 20, targetAK.maxX + 15))
        let posY = max(screen.visibleFrame.minY + 20, min(screen.visibleFrame.maxY - panelH - 20, targetAK.midY))

        let win = NSWindow(contentRect: NSRect(x: posX, y: posY, width: panelW, height: panelH),
                           styleMask: [.titled, .closable, .nonactivatingPanel],
                           backing: .buffered, defer: false)
        win.title = "Скролл-скриншот"
        win.level = .floating
        win.isReleasedWhenClosed = false
        win.backgroundColor = NSColor(red: 0.12, green: 0.13, blue: 0.15, alpha: 1.0)

        win.contentView = NSHostingView(rootView: ScrollControlPanelView(
            frameCount: capturedFrames.count,
            onAddFrame: { [weak self] in self?.captureCurrentFrame() },
            onFinish: { [weak self] in self?.finishSession() },
            onCancel: { [weak self] in self?.cancelSession() }
        ))

        win.makeKeyAndOrderFront(nil)
        self.panelWindow = win
    }

    private func updatePanel() {
        panelWindow?.contentView = NSHostingView(rootView: ScrollControlPanelView(
            frameCount: capturedFrames.count,
            onAddFrame: { [weak self] in self?.captureCurrentFrame() },
            onFinish: { [weak self] in self?.finishSession() },
            onCancel: { [weak self] in self?.cancelSession() }
        ))
    }

    private func closePanel() {
        panelWindow?.orderOut(nil)
        panelWindow = nil
    }
}

struct ScrollControlPanelView: View {
    var frameCount: Int
    var onAddFrame: () -> Void
    var onFinish: () -> Void
    var onCancel: () -> Void

    var body: some View {
        VStack(spacing: 8) {
            HStack {
                Circle().fill(Color.green).frame(width: 8, height: 8)
                Text("Кадров добавлено: \(frameCount)")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.white)
                Spacer()
            }

            Text("Прокрутите страницу вниз и нажмите «+ Кадр»")
                .font(.system(size: 10))
                .foregroundColor(.gray)
                .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 8) {
                Button(action: onAddFrame) {
                    HStack(spacing: 4) {
                        Image(systemName: "plus.viewfinder")
                        Text("+ Кадр")
                    }
                    .font(.system(size: 11, weight: .bold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    .background(Color.blue)
                    .foregroundColor(.white)
                    .cornerRadius(5)
                }

                Button(action: onFinish) {
                    HStack(spacing: 4) {
                        Image(systemName: "checkmark")
                        Text("Готово")
                    }
                    .font(.system(size: 11, weight: .bold))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Color.green)
                    .foregroundColor(.white)
                    .cornerRadius(5)
                }

                Button("Отмена", action: onCancel)
                    .font(.system(size: 11))
                    .foregroundColor(.gray)
                    .padding(.horizontal, 6)
            }
            .buttonStyle(.plain)
        }
        .padding(10)
        .background(Color(red: 0.12, green: 0.13, blue: 0.15))
    }
}

// --- Beautify Пресеты ---
enum GradientPreset: String, CaseIterable, Identifiable {
    case nebula = "Nebula", sunset = "Sunset", ocean = "Ocean", slate = "Slate", border = "Border"
    var id: String { rawValue }

    var gradient: LinearGradient {
        switch self {
        case .nebula:
            return LinearGradient(colors: [Color(red: 0.45, green: 0.20, blue: 0.95), Color(red: 0.85, green: 0.25, blue: 0.65)], startPoint: .topLeading, endPoint: .bottomTrailing)
        case .sunset:
            return LinearGradient(colors: [Color(red: 0.98, green: 0.40, blue: 0.25), Color(red: 0.85, green: 0.15, blue: 0.55)], startPoint: .topLeading, endPoint: .bottomTrailing)
        case .ocean:
            return LinearGradient(colors: [Color(red: 0.10, green: 0.60, blue: 0.95), Color(red: 0.15, green: 0.85, blue: 0.75)], startPoint: .topLeading, endPoint: .bottomTrailing)
        case .slate:
            return LinearGradient(colors: [Color(red: 0.16, green: 0.18, blue: 0.22), Color(red: 0.08, green: 0.09, blue: 0.11)], startPoint: .topLeading, endPoint: .bottomTrailing)
        case .border:
            return LinearGradient(colors: [Color.white.opacity(0.15), Color.white.opacity(0.05)], startPoint: .topLeading, endPoint: .bottomTrailing)
        }
    }
}

struct WindowTarget {
    let id: CGWindowID
    let appKitFrame: CGRect
    let quartzFrame: CGRect
    let ownerName: String
}

final class WindowDetector {
    /// Все окна верхнего уровня на всех экранах; appKitFrame — глобальные AppKit-координаты
    static func getVisibleWindows() -> [WindowTarget] {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        var result: [WindowTarget] = []
        for dict in list {
            guard let layer = dict[kCGWindowLayer as String] as? Int, layer == 0,
                  let wid = dict[kCGWindowNumber as String] as? CGWindowID,
                  let boundsDict = dict[kCGWindowBounds as String] as? [String: Any],
                  let qRect = CGRect(dictionaryRepresentation: boundsDict as CFDictionary) else { continue }
            let owner = dict[kCGWindowOwnerName as String] as? String ?? ""
            if qRect.width > 60 && qRect.height > 60 {
                result.append(WindowTarget(id: wid, appKitFrame: Coord.toAppKit(qRect), quartzFrame: qRect, ownerName: owner))
            }
        }
        return result
    }
}

@MainActor
final class CaptureEngine {
    static let shared = CaptureEngine()
    private init() {}

    func capture(quartzRect: CGRect) -> NSImage? {
        guard quartzRect.width > 2 && quartzRect.height > 2,
              let cgImage = CGWindowListCreateImage(quartzRect, .optionOnScreenOnly, kCGNullWindowID, .bestResolution) else { return nil }
        playShutterSound()
        return NSImage(cgImage: cgImage, size: quartzRect.size)
    }

    func captureWindow(target: WindowTarget) -> NSImage? {
        if let cgImage = CGWindowListCreateImage(.null, .optionIncludingWindow, target.id, [.bestResolution, .boundsIgnoreFraming]) {
            playShutterSound()
            let scale = Coord.screen(for: target.appKitFrame).backingScaleFactor
            let size = NSSize(width: CGFloat(cgImage.width) / scale, height: CGFloat(cgImage.height) / scale)
            return NSImage(cgImage: cgImage, size: size)
        }
        return capture(quartzRect: target.quartzFrame)
    }

    /// Экран под курсором целиком
    func captureFullScreen() -> NSImage? {
        let screen = Coord.screen(containing: NSEvent.mouseLocation)
        return capture(quartzRect: Coord.toQuartz(screen.frame))
    }
}

final class OCREngine {
    static func extractText(from image: NSImage) -> String {
        guard let tiffData = image.tiffRepresentation,
              let ciImage = CIImage(data: tiffData) else { return "" }
        let handler = VNImageRequestHandler(ciImage: ciImage, options: [:])
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        do {
            try handler.perform([request])
            guard let observations = request.results else { return "" }
            return observations.compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
        } catch { return "" }
    }
}

func generatePixellatedImage(from image: NSImage) -> NSImage {
    guard let tiff = image.tiffRepresentation,
          let ciImage = CIImage(data: tiff) else { return image }
    let filter = CIFilter(name: "CIPixellate")
    filter?.setValue(ciImage, forKey: kCIInputImageKey)
    filter?.setValue(16.0, forKey: kCIInputScaleKey)
    let context = CIContext()
    guard let outputCI = filter?.outputImage,
          let cgImage = context.createCGImage(outputCI, from: ciImage.extent) else { return image }
    return NSImage(cgImage: cgImage, size: image.size)
}

// --- Панель управления записью: прибита к нижнему краю зоны, до старта — «● Запись» ---
struct RecordingControlBarView: View {
    @ObservedObject var recorder = ScreenRecorder.shared
    @State private var elapsedSeconds: Int = 0
    let timer = Timer.publish(every: 0.25, on: .main, in: .common).autoconnect()

    var timeString: String {
        String(format: "%02d:%02d", elapsedSeconds / 60, elapsedSeconds % 60)
    }

    var body: some View {
        HStack(spacing: 8) {
            if recorder.isRecording {
                HStack(spacing: 6) {
                    Circle()
                        .fill(recorder.isPaused ? Color.yellow : Color.red)
                        .frame(width: 10, height: 10)
                    Text(timeString)
                        .font(.system(size: 13, weight: .bold, design: .monospaced))
                        .foregroundColor(.white)
                }
                .padding(.leading, 6)

                Button(action: { recorder.togglePause() }) {
                    Image(systemName: recorder.isPaused ? "play.fill" : "pause.fill")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundColor(.white)
                        .frame(width: 26, height: 26)
                        .background(Color.white.opacity(0.15))
                        .cornerRadius(5)
                }
                .buttonStyle(.plain)
                .help(recorder.isPaused ? "Возобновить запись" : "Пауза записи")

                Button(action: { recorder.toggleMicrophone() }) {
                    Image(systemName: recorder.isAudioMuted ? "mic.slash.fill" : "mic.fill")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundColor(recorder.isAudioMuted ? .red : .green)
                        .frame(width: 26, height: 26)
                        .background(Color.white.opacity(0.15))
                        .cornerRadius(5)
                }
                .buttonStyle(.plain)
                .help(recorder.isAudioMuted ? "Включить микрофон" : "Отключить микрофон")
            } else {
                Button(action: { recorder.beginCapture() }) {
                    HStack(spacing: 5) {
                        Circle().fill(Color.white).frame(width: 8, height: 8)
                        Text("Запись")
                            .font(.system(size: 11, weight: .bold))
                    }
                    .padding(.horizontal, 10)
                    .frame(height: 26)
                    .background(Color.red)
                    .foregroundColor(.white)
                    .cornerRadius(5)
                }
                .buttonStyle(.plain)
                .help("Начать запись")
            }

            Button(action: { recorder.addLiveBlurZone() }) {
                Image(systemName: "checkerboard.rectangle")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundColor(.white)
                    .frame(width: 26, height: 26)
                    .background(Color.white.opacity(0.15))
                    .cornerRadius(5)
            }
            .buttonStyle(.plain)
            .help("Добавить зону размытия (Live Blur)")

            if recorder.isRecording {
                Button(action: { recorder.stopRecording() }) {
                    HStack(spacing: 4) {
                        Image(systemName: "stop.fill").font(.system(size: 9))
                        Text("Стоп")
                            .font(.system(size: 11, weight: .bold))
                    }
                    .padding(.horizontal, 8)
                    .frame(height: 26)
                    .background(Color.red)
                    .foregroundColor(.white)
                    .cornerRadius(5)
                }
                .buttonStyle(.plain)
            }

            Button(action: { recorder.cancelRecording() }) {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundColor(.gray)
                    .padding(4)
            }
            .buttonStyle(.plain)
            .help("Отменить")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(red: 0.12, green: 0.13, blue: 0.16))
                .shadow(color: .black.opacity(0.6), radius: 10, x: 0, y: 4)
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.15), lineWidth: 1))
        )
        .fixedSize()
        .onReceive(timer) { _ in
            elapsedSeconds = Int(recorder.activeElapsedSeconds())
        }
    }
}

// --- 8-направленная рамка (общая для зоны записи и блюр-зон) ---
enum FrameStyle { case record, blur }

final class ResizableFrameView: NSView {
    static let margin: CGFloat = 14
    let style: FrameStyle
    let bodyDrag: Bool
    var onFrameChanged: (CGRect) -> Void          // inner rect, AppKit global
    var onClose: (() -> Void)?
    private var dragHandle: DragHandle?
    private var initialMouseLocation: NSPoint = .zero
    private var initialWindowFrame: NSRect = .zero
    private var trackingArea: NSTrackingArea?

    enum DragHandle { case top, bottom, left, right, topLeft, topRight, bottomLeft, bottomRight, body, none }

    init(style: FrameStyle, bodyDrag: Bool, onFrameChanged: @escaping (CGRect) -> Void) {
        self.style = style
        self.bodyDrag = bodyDrag
        self.onFrameChanged = onFrameChanged
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea { removeTrackingArea(ta) }
        let ta = NSTrackingArea(rect: bounds, options: [.mouseMoved, .cursorUpdate, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(ta)
        self.trackingArea = ta
    }

    private var closeRect: NSRect {
        let m = Self.margin
        return NSRect(x: m + 4, y: bounds.height - m - 4 - 16, width: 16, height: 16)
    }

    private func getHandle(at pt: NSPoint) -> DragHandle {
        let margin = Self.margin
        let hs: CGFloat = 20
        let topBarH: CGFloat = 24

        if pt.x <= margin + hs && pt.y >= bounds.height - margin - hs { return .topLeft }
        if pt.x >= bounds.width - margin - hs && pt.y >= bounds.height - margin - hs { return .topRight }
        if pt.x <= margin + hs && pt.y <= margin + hs { return .bottomLeft }
        if pt.x >= bounds.width - margin - hs && pt.y <= margin + hs { return .bottomRight }

        if !bodyDrag {
            let topBarRect = NSRect(x: bounds.midX - 80, y: bounds.height - margin, width: 160, height: topBarH)
            if topBarRect.contains(pt) { return .body }
        }

        if pt.y >= bounds.height - margin - 8 && pt.y <= bounds.height { return .top }
        if pt.y <= margin + 8 && pt.y >= 0 { return .bottom }
        if pt.x <= margin + 8 && pt.x >= 0 { return .left }
        if pt.x >= bounds.width - margin - 8 && pt.x <= bounds.width { return .right }

        return bodyDrag ? .body : .none
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let pt = convert(point, from: nil)
        return getHandle(at: pt) != .none ? self : nil  // у рамки записи внутри — прозрачно для мыши
    }

    override func mouseMoved(with event: NSEvent) {
        let pt = convert(event.locationInWindow, from: nil)
        switch getHandle(at: pt) {
        case .topLeft, .bottomRight:
            NSCursor(image: NSImage(systemSymbolName: "arrow.up.left.and.arrow.down.right", accessibilityDescription: nil) ?? NSImage(), hotSpot: NSPoint(x: 8, y: 8)).set()
        case .topRight, .bottomLeft:
            NSCursor(image: NSImage(systemSymbolName: "arrow.up.right.and.arrow.down.left", accessibilityDescription: nil) ?? NSImage(), hotSpot: NSPoint(x: 8, y: 8)).set()
        case .top, .bottom:
            NSCursor.resizeUpDown.set()
        case .left, .right:
            NSCursor.resizeLeftRight.set()
        case .body:
            NSCursor.openHand.set()
        case .none:
            NSCursor.arrow.set()
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        let margin = Self.margin
        let innerRect = bounds.insetBy(dx: margin, dy: margin)

        switch style {
        case .record:
            let borderPath = NSBezierPath(rect: innerRect)
            borderPath.lineWidth = 2.5
            NSColor.systemRed.setStroke()
            borderPath.stroke()
            drawHandles(innerRect, size: 10, stroke: .systemRed)

            let topBarRect = NSRect(x: bounds.midX - 80, y: bounds.height - margin + 2, width: 160, height: 20)
            NSColor.black.withAlphaComponent(0.85).setFill()
            let barPath = NSBezierPath(roundedRect: topBarRect, xRadius: 5, yRadius: 5)
            barPath.fill()
            NSColor.white.withAlphaComponent(0.2).setStroke()
            barPath.lineWidth = 1
            barPath.stroke()

            let title = "⠿ Зажмите для смещения" as NSString
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 9, weight: .bold), .foregroundColor: NSColor.white]
            let tSize = title.size(withAttributes: attrs)
            title.draw(at: NSPoint(x: topBarRect.midX - tSize.width / 2, y: topBarRect.midY - tSize.height / 2), withAttributes: attrs)

        case .blur:
            // Ненавязчиво: содержимое под зоной видно, только тонкая пунктирная рамка + маленькие ручки. alpha 0.01 — чтобы тело ловило мышь
            NSColor.black.withAlphaComponent(0.01).setFill()
            innerRect.fill()
            let dash = NSBezierPath(rect: innerRect)
            dash.lineWidth = 1
            dash.setLineDash([4, 3], count: 2, phase: 0)
            NSColor.white.withAlphaComponent(0.9).setStroke()
            dash.stroke()
            let dash2 = NSBezierPath(rect: innerRect)
            dash2.lineWidth = 1
            dash2.setLineDash([4, 3], count: 2, phase: 4)
            NSColor.black.withAlphaComponent(0.5).setStroke()
            dash2.stroke()
            drawHandles(innerRect, size: 6, stroke: NSColor.black.withAlphaComponent(0.6))

            let x = closeRect
            NSColor.black.withAlphaComponent(0.55).setFill()
            NSBezierPath(roundedRect: x, xRadius: 3, yRadius: 3).fill()
            let t = "✕" as NSString
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 10, weight: .bold), .foregroundColor: NSColor.white]
            let ts = t.size(withAttributes: attrs)
            t.draw(at: NSPoint(x: x.midX - ts.width / 2, y: x.midY - ts.height / 2), withAttributes: attrs)
        }
    }

    private func drawHandles(_ innerRect: NSRect, size hs: CGFloat, stroke: NSColor) {
        let handlePoints = [
            NSPoint(x: innerRect.minX, y: innerRect.maxY), NSPoint(x: innerRect.maxX, y: innerRect.maxY),
            NSPoint(x: innerRect.minX, y: innerRect.minY), NSPoint(x: innerRect.maxX, y: innerRect.minY),
            NSPoint(x: innerRect.midX, y: innerRect.maxY), NSPoint(x: innerRect.midX, y: innerRect.minY),
            NSPoint(x: innerRect.minX, y: innerRect.midY), NSPoint(x: innerRect.maxX, y: innerRect.midY)
        ]
        for pt in handlePoints {
            let hr = NSRect(x: pt.x - hs / 2, y: pt.y - hs / 2, width: hs, height: hs)
            let p = NSBezierPath(roundedRect: hr, xRadius: 2, yRadius: 2)
            NSColor.white.setFill()
            p.fill()
            stroke.setStroke()
            p.lineWidth = 1.5
            p.stroke()
        }
    }

    override func mouseDown(with event: NSEvent) {
        guard let win = window else { return }
        let pt = convert(event.locationInWindow, from: nil)
        if style == .blur && closeRect.contains(pt) { onClose?(); return }
        initialMouseLocation = NSEvent.mouseLocation
        initialWindowFrame = win.frame
        dragHandle = getHandle(at: pt)
        if dragHandle == .body { NSCursor.closedHand.set() }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let win = window, let handle = dragHandle, handle != .none else { return }

        let currentMouse = NSEvent.mouseLocation
        let dx = currentMouse.x - initialMouseLocation.x
        let dy = currentMouse.y - initialMouseLocation.y
        var newFrame = initialWindowFrame

        switch handle {
        case .body:
            newFrame.origin.x += dx
            newFrame.origin.y += dy
        case .topLeft:
            newFrame.origin.x += dx
            newFrame.size.width -= dx
            newFrame.size.height += dy
        case .topRight:
            newFrame.size.width += dx
            newFrame.size.height += dy
        case .bottomLeft:
            newFrame.origin.x += dx
            newFrame.origin.y += dy
            newFrame.size.width -= dx
            newFrame.size.height -= dy
        case .bottomRight:
            newFrame.origin.y += dy
            newFrame.size.width += dx
            newFrame.size.height -= dy
        case .top:
            newFrame.size.height += dy
        case .bottom:
            newFrame.origin.y += dy
            newFrame.size.height -= dy
        case .left:
            newFrame.origin.x += dx
            newFrame.size.width -= dx
        case .right:
            newFrame.size.width += dx
        case .none: break
        }

        let minW: CGFloat = style == .record ? 120 : 60
        let minH: CGFloat = style == .record ? 80 : 40
        if newFrame.width > minW + 2 * Self.margin && newFrame.height > minH + 2 * Self.margin {
            win.setFrame(newFrame, display: true)
            needsDisplay = true
            onFrameChanged(win.frame.insetBy(dx: Self.margin, dy: Self.margin))
        }
    }

    override func mouseUp(with event: NSEvent) {
        dragHandle = nil
        NSCursor.arrow.set()
    }
}

/// Плавающая рамка без активации: базовый класс для зоны записи и блюр-зон
class FloatingFrameWindow: NSPanel {
    let frameView: ResizableFrameView

    init(contentAppKitRect: NSRect, style: FrameStyle, bodyDrag: Bool, onFrameChanged: @escaping (CGRect) -> Void) {
        let outerRect = contentAppKitRect.insetBy(dx: -ResizableFrameView.margin, dy: -ResizableFrameView.margin)
        frameView = ResizableFrameView(style: style, bodyDrag: bodyDrag, onFrameChanged: onFrameChanged)
        super.init(contentRect: outerRect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        self.isOpaque = false
        self.backgroundColor = .clear
        self.level = .statusBar
        self.hasShadow = false
        self.isFloatingPanel = true
        self.becomesKeyOnlyIfNeeded = true
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        self.acceptsMouseMovedEvents = true
        self.contentView = frameView
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    /// Зона (без полей) в AppKit-координатах
    var zone: CGRect { frame.insetBy(dx: ResizableFrameView.margin, dy: ResizableFrameView.margin) }

    func offset(dx: CGFloat, dy: CGFloat) {
        var f = frame
        f.origin.x += dx; f.origin.y += dy
        setFrame(f, display: true)
        frameView.onFrameChanged(zone)
    }
}

final class LiveResizableFrameWindow: FloatingFrameWindow {
    init(contentAppKitRect: NSRect, onFrameChanged: @escaping (CGRect) -> Void) {
        super.init(contentAppKitRect: contentAppKitRect, style: .record, bodyDrag: false, onFrameChanged: onFrameChanged)
    }
}

/// Live Blur: на экране — тонкая рамка, в записи — пикселизация этой области (композитор)
final class LiveBlurZoneWindow: FloatingFrameWindow {
    init(contentAppKitRect: NSRect, onFrameChanged: @escaping (CGRect) -> Void, onClose: @escaping () -> Void) {
        super.init(contentAppKitRect: contentAppKitRect, style: .blur, bodyDrag: true, onFrameChanged: onFrameChanged)
        self.hasShadow = false
        frameView.onClose = onClose
    }
}

// --- Поток одного дисплея: держит последний кадр ---
final class DisplayStream: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    let display: SCDisplay
    let quartzFrame: CGRect          // точки, Quartz-глобально
    let scale: CGFloat               // пикселей на point
    private var stream: SCStream?
    private let lock = NSLock()
    private var latest: CIImage?

    init(display: SCDisplay, scale: CGFloat) {
        self.display = display
        self.quartzFrame = display.frame
        self.scale = scale
    }

    func start(fps: Int, showCursor: Bool) async throws {
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.width = Int(CGFloat(display.width) * scale)
        config.height = Int(CGFloat(display.height) * scale)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value: 1, timescale: Int32(fps))
        config.showsCursor = showCursor
        config.queueDepth = 5
        let s = SCStream(filter: filter, configuration: config, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: DispatchQueue(label: "com.qscreen.capture.\(display.displayID)", qos: .userInteractive))
        try await s.startCapture()
        stream = s
    }

    func stop() async {
        try? await stream?.stopCapture()
        stream = nil
    }

    var latestImage: CIImage? { lock.lock(); defer { lock.unlock() }; return latest }

    nonisolated func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let statusRaw = attachments.first?[.status] as? Int,
              let status = SCFrameStatus(rawValue: statusRaw), status == .complete,
              let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let img = CIImage(cvPixelBuffer: buffer)  // CIImage удерживает буфер
        lock.lock(); latest = img; lock.unlock()
    }

    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        print("SCStream stopped: \(error)")
    }
}

enum RecorderState { case idle, armed, recording }

// --- Движок записи: SCK по дисплею на каждый → композиция в холст (crop + zoom + пикселизация) → AVAssetWriter ---
final class ScreenRecorder: NSObject, ObservableObject, @unchecked Sendable, AVCaptureAudioDataOutputSampleBufferDelegate {
    static let shared = ScreenRecorder()

    @Published @MainActor var isRecording = false
    @Published @MainActor var isArmed = false
    @Published @MainActor var isPaused = false
    @Published @MainActor var isAudioMuted = false
    @MainActor var onStateChange: ((RecorderState) -> Void)?

    private var displays: [DisplayStream] = []
    private var assetWriter: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var pixelBufferAdaptor: AVAssetWriterInputPixelBufferAdaptor?
    private var audioSession: AVCaptureSession?
    private var composeTimer: DispatchSourceTimer?
    private let composeQueue = DispatchQueue(label: "com.qscreen.compose", qos: .userInteractive)

    private let ciContext = CIContext(options: [.useSoftwareRenderer: false])
    private var canvasSize = CGSize(width: 1920, height: 1080)

    // Разделяемое состояние (главный поток пишет, compose/audio читают)
    private let sessionLock = NSLock()
    private var cropAppKit: CGRect = .zero
    private var blurRectsAppKit: [CGRect] = []
    private var primaryTop: CGFloat = 0           // maxY primary-экрана, чтобы не трогать NSScreen из compose-очереди
    private var isPausedInternal = false
    private var isAudioMutedInternal = false
    private var basePTS: CMTime = .zero          // host time старта
    private var pauseStart: CMTime = .zero
    private var totalPauseOffset: CMTime = .zero
    private var lastVideoPTS: CMTime = .invalid

    private var floatingBarWindow: NSPanel?
    private var liveFrameWindow: LiveResizableFrameWindow?
    private var blurWindows: [LiveBlurZoneWindow] = []
    private var outputFileURL: URL?

    private static func hostNow() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    /// Секунды активной записи (без пауз)
    func activeElapsedSeconds() -> Double {
        sessionLock.lock(); defer { sessionLock.unlock() }
        guard basePTS != .zero else { return 0 }
        var t = CMTimeSubtract(CMTimeSubtract(Self.hostNow(), basePTS), totalPauseOffset)
        if isPausedInternal { t = CMTimeSubtract(t, CMTimeSubtract(Self.hostNow(), pauseStart)) }
        return max(0, CMTimeGetSeconds(t))
    }

    // ---------- Вооружение: рамка + панель, запись ещё не идёт ----------
    @MainActor
    func arm(initialAppKitRect: CGRect) {
        guard !isRecording, !isArmed else { return }
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
            let alert = NSAlert()
            alert.messageText = "Нужно разрешение на запись экрана"
            alert.informativeText = "Системные настройки → Конфиденциальность и безопасность → Запись экрана и системного звука → включить QScreen, затем перезапустить приложение."
            alert.runModal()
            return
        }
        AppDelegate.shared?.closeEditor()
        AppDelegate.shared?.closeSettings()

        sessionLock.lock(); cropAppKit = initialAppKitRect; blurRectsAppKit = []; basePTS = .zero; primaryTop = Coord.primary.frame.maxY; sessionLock.unlock()
        isArmed = true; isPaused = false; isAudioMuted = false
        showFloatingBar()
        showLiveFrame(appKitRect: initialAppKitRect)
        placeBar(near: initialAppKitRect)
        onStateChange?(.armed)
    }

    // ---------- Реальный старт ----------
    @MainActor
    func beginCapture() {
        guard isArmed, !isRecording else { return }
        let rect: CGRect
        sessionLock.lock(); rect = cropAppKit; sessionLock.unlock()
        Task {
            do {
                try await startCapturePipeline(appKitRect: rect)
            } catch {
                await abortPipeline()
                await MainActor.run {
                    self.teardownUI()
                    self.isArmed = false
                    self.isRecording = false
                    self.onStateChange?(.idle)
                    let alert = NSAlert()
                    alert.messageText = "Не удалось запустить запись"
                    let ns = error as NSError
                    alert.informativeText = ns.domain.contains("ScreenCaptureKit") && ns.code == -3801
                        ? "Нет разрешения на запись экрана.\nСистемные настройки → Конфиденциальность и безопасность → Запись экрана и системного звука → включить QScreen, затем перезапустить приложение."
                        : "\(error)"
                    alert.runModal()
                }
            }
        }
    }

    private func startCapturePipeline(appKitRect: CGRect) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard !content.displays.isEmpty else { throw NSError(domain: "QScreen", code: 1, userInfo: [NSLocalizedDescriptionKey: "Нет дисплеев для захвата"]) }

        // Все дисплеи — зона может уехать на любой или лежать на стыке
        var streams: [DisplayStream] = []
        for d in content.displays {
            let screen = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == d.displayID }
            streams.append(DisplayStream(display: d, scale: screen?.backingScaleFactor ?? 2.0))
        }

        let zoneScale = await MainActor.run { Coord.screen(for: appKitRect).backingScaleFactor }
        let targetW = max(640, (Int(appKitRect.width * zoneScale) / 2) * 2)
        let targetH = max(360, (Int(appKitRect.height * zoneScale) / 2) * 2)
        canvasSize = CGSize(width: targetW, height: targetH)

        let fpsSetting = UserDefaults.standard.integer(forKey: "videoFPS")
        let fps = fpsSetting > 0 ? fpsSetting : 60
        let showCursor = UserDefaults.standard.object(forKey: "videoShowCursor") != nil ? UserDefaults.standard.bool(forKey: "videoShowCursor") : true

        let videoExt = UserDefaults.standard.string(forKey: "videoFormat") ?? "mp4"
        let fileURL = FilenameHelper.getDefaultSaveFolder().appendingPathComponent(FilenameHelper.generateFilename(ext: videoExt))
        self.outputFileURL = fileURL
        try? FileManager.default.removeItem(at: fileURL)

        let writer = try AVAssetWriter(outputURL: fileURL, fileType: videoExt == "mov" ? .mov : .mp4)
        let videoCodecChoice = UserDefaults.standard.string(forKey: "videoCodec") ?? "hevc"
        let codecType: AVVideoCodecType = (videoCodecChoice == "h264") ? .h264 : .hevc

        let videoSettings: [String: Any] = [
            AVVideoCodecKey: codecType,
            AVVideoWidthKey: Int(canvasSize.width),
            AVVideoHeightKey: Int(canvasSize.height)
        ]
        let vInput = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings)
        vInput.expectsMediaDataInRealTime = true

        let sourceBufferAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(canvasSize.width),
            kCVPixelBufferHeightKey as String: Int(canvasSize.height),
            kCVPixelBufferMetalCompatibilityKey as String: true
        ]
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vInput, sourcePixelBufferAttributes: sourceBufferAttributes)
        if writer.canAdd(vInput) { writer.add(vInput) }

        let recordAudio = UserDefaults.standard.bool(forKey: "videoRecordAudio")
        if recordAudio {
            let audioSettings: [String: Any] = [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 44100,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 128000
            ]
            let aInput = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings)
            aInput.expectsMediaDataInRealTime = true
            if writer.canAdd(aInput) { writer.add(aInput) }
            self.audioInput = aInput
        }

        writer.startWriting()
        writer.startSession(atSourceTime: .zero)
        installWriter(writer, vInput, adaptor)

        // Потоки регистрируем по мере старта — при ошибке abortPipeline остановит уже запущенные
        self.displays = []
        for s in streams {
            try await s.start(fps: fps, showCursor: showCursor)
            self.displays.append(s)
        }
        if recordAudio { startMicrophoneCapture() }

        let timer = DispatchSource.makeTimerSource(queue: composeQueue)
        timer.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(2))
        timer.setEventHandler { [weak self] in self?.composeAndAppend() }
        timer.resume()
        self.composeTimer = timer

        await MainActor.run {
            self.isRecording = true
            self.isArmed = false
            self.isPaused = false
            self.isAudioMuted = false
            self.onStateChange?(.recording)
        }
    }

    private func installWriter(_ writer: AVAssetWriter, _ vInput: AVAssetWriterInput, _ adaptor: AVAssetWriterInputPixelBufferAdaptor) {
        sessionLock.lock()
        assetWriter = writer
        videoInput = vInput
        pixelBufferAdaptor = adaptor
        isPausedInternal = false
        isAudioMutedInternal = false
        totalPauseOffset = .zero
        basePTS = Self.hostNow()
        lastVideoPTS = .invalid
        sessionLock.unlock()
    }

    /// Откат неудачного старта: потоки, writer, недописанный файл
    private func abortPipeline() async {
        composeTimer?.cancel(); composeTimer = nil
        audioSession?.stopRunning(); audioSession = nil
        for d in displays { await d.stop() }
        displays = []
        if let w = detachWriter(), w.status == .writing { w.cancelWriting() }
        if let f = outputFileURL { try? FileManager.default.removeItem(at: f) }
    }

    private func detachWriter() -> AVAssetWriter? {
        sessionLock.lock(); defer { sessionLock.unlock() }
        let writer = assetWriter
        assetWriter = nil; videoInput = nil; audioInput = nil; pixelBufferAdaptor = nil
        basePTS = .zero
        return writer
    }

    private func startMicrophoneCapture() {
        guard let mic = AVCaptureDevice.default(for: .audio),
              let input = try? AVCaptureDeviceInput(device: mic) else { return }
        let session = AVCaptureSession()
        if session.canAddInput(input) { session.addInput(input) }

        let output = AVCaptureAudioDataOutput()
        output.setSampleBufferDelegate(self, queue: DispatchQueue(label: "com.qscreen.audiocapture"))
        if session.canAddOutput(output) { session.addOutput(output) }

        session.startRunning()
        self.audioSession = session
    }

    // Аудио: PTS = hostTime − старт − паузы (та же шкала, что у видео)
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard sampleBuffer.isValid else { return }

        sessionLock.lock()
        defer { sessionLock.unlock() }

        guard basePTS != .zero, !isPausedInternal, !isAudioMutedInternal,
              let aInput = self.audioInput, aInput.isReadyForMoreMediaData,
              let writer = self.assetWriter, writer.status == .writing else { return }

        let rawPTS = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard rawPTS.isValid else { return }
        let pts = CMTimeSubtract(CMTimeSubtract(rawPTS, basePTS), totalPauseOffset)
        guard pts >= .zero else { return }

        var timingInfo = CMSampleTimingInfo(duration: CMSampleBufferGetDuration(sampleBuffer), presentationTimeStamp: pts, decodeTimeStamp: .invalid)
        var newSampleBuffer: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: sampleBuffer, sampleTimingEntryCount: 1, sampleTimingArray: &timingInfo, sampleBufferOut: &newSampleBuffer)
        if status == noErr, let b = newSampleBuffer { aInput.append(b) }
    }

    // ---------- Кадр: композиция всех дисплеев в холст (crop + zoom) + пикселизация блюр-зон ----------
    private func composeAndAppend() {
        sessionLock.lock()
        if isPausedInternal || basePTS == .zero { sessionLock.unlock(); return }
        let crop = cropAppKit
        let blurs = blurRectsAppKit
        let top = primaryTop
        let pts = CMTimeSubtract(CMTimeSubtract(Self.hostNow(), basePTS), totalPauseOffset)
        let adaptor = pixelBufferAdaptor
        let vInput = videoInput
        let writer = assetWriter
        let canvas = canvasSize
        let last = lastVideoPTS
        sessionLock.unlock()

        guard let adaptor = adaptor, let vInput = vInput, vInput.isReadyForMoreMediaData,
              let writer = writer, writer.status == .writing else { return }
        if last.isValid && pts <= last { return }

        func toQ(_ ak: CGRect) -> CGRect { CGRect(x: ak.minX, y: top - ak.maxY, width: ak.width, height: ak.height) }
        let cropQ = toQ(crop)
        guard cropQ.width > 1, cropQ.height > 1 else { return }
        let canvasRect = CGRect(origin: .zero, size: canvas)
        let sx = canvas.width / cropQ.width      // пикселей холста на point
        let sy = canvas.height / cropQ.height

        var image = CIImage(color: .black).cropped(to: canvasRect)
        for ds in displays {
            guard let src = ds.latestImage else { continue }
            let inter = cropQ.intersection(ds.quartzFrame)
            guard !inter.isNull, inter.width >= 1, inter.height >= 1 else { continue }
            // Пиксели дисплея, origin снизу-слева (CIImage)
            let srcPx = CGRect(x: (inter.minX - ds.quartzFrame.minX) * ds.scale,
                               y: (ds.quartzFrame.maxY - inter.maxY) * ds.scale,
                               width: inter.width * ds.scale, height: inter.height * ds.scale)
            let dstX = (inter.minX - cropQ.minX) * sx
            let dstY = (cropQ.maxY - inter.maxY) * sy
            let piece = src.cropped(to: srcPx)
                .transformed(by: CGAffineTransform(translationX: -srcPx.minX, y: -srcPx.minY))
                .transformed(by: CGAffineTransform(scaleX: sx / ds.scale, y: sy / ds.scale))
                .transformed(by: CGAffineTransform(translationX: dstX, y: dstY))
            image = piece.composited(over: image)
        }

        // Блюр-зоны: CIPixellate scale=16 по области холста
        for z in blurs {
            let zq = toQ(z)
            let inter = cropQ.intersection(zq)
            guard !inter.isNull, inter.width >= 1, inter.height >= 1 else { continue }
            let region = CGRect(x: (inter.minX - cropQ.minX) * sx, y: (cropQ.maxY - inter.maxY) * sy, width: inter.width * sx, height: inter.height * sy)
            guard let f = CIFilter(name: "CIPixellate") else { continue }
            f.setValue(image.cropped(to: region), forKey: kCIInputImageKey)
            f.setValue(16.0, forKey: kCIInputScaleKey)
            f.setValue(CIVector(x: region.minX, y: region.minY), forKey: kCIInputCenterKey)
            if let out = f.outputImage?.cropped(to: region) { image = out.composited(over: image) }
        }
        image = image.cropped(to: canvasRect)

        guard let pool = adaptor.pixelBufferPool else { return }
        var dest: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &dest)
        guard let buffer = dest else { return }
        ciContext.render(image, to: buffer, bounds: canvasRect, colorSpace: CGColorSpaceCreateDeviceRGB())
        if adaptor.append(buffer, withPresentationTime: pts) {
            sessionLock.lock(); lastVideoPTS = pts; sessionLock.unlock()
        }
    }

    // ---------- Управление ----------
    @MainActor
    func updateCropRect(newAppKitRect: CGRect) {
        sessionLock.lock()
        cropAppKit = newAppKitRect
        sessionLock.unlock()
    }

    /// Перенос рамки (размер не изменился) тащит блюр-зоны; ресайз зоны не трогает
    @MainActor
    private func onFrameChanged(_ newRect: CGRect) {
        let old: CGRect
        sessionLock.lock(); old = cropAppKit; sessionLock.unlock()
        if abs(old.width - newRect.width) < 0.5 && abs(old.height - newRect.height) < 0.5 && old.origin != newRect.origin {
            let dx = newRect.minX - old.minX, dy = newRect.minY - old.minY
            for w in blurWindows { w.offset(dx: dx, dy: dy) }
        }
        updateCropRect(newAppKitRect: newRect)
        placeBar(near: newRect)
    }

    @MainActor
    private func syncBlurRects() {
        sessionLock.lock()
        blurRectsAppKit = blurWindows.map { $0.zone }
        sessionLock.unlock()
    }

    @MainActor
    func togglePause() {
        guard isRecording else { return }
        isPaused.toggle()
        sessionLock.lock()
        isPausedInternal = isPaused
        if isPaused {
            pauseStart = Self.hostNow()
        } else {
            totalPauseOffset = CMTimeAdd(totalPauseOffset, CMTimeSubtract(Self.hostNow(), pauseStart))
        }
        sessionLock.unlock()
    }

    @MainActor
    func toggleMicrophone() {
        isAudioMuted.toggle()
        sessionLock.lock()
        isAudioMutedInternal = isAudioMuted
        sessionLock.unlock()
    }

    @MainActor
    func addLiveBlurZone() {
        let crop: CGRect
        sessionLock.lock(); crop = cropAppKit; sessionLock.unlock()
        let rect = CGRect(x: crop.midX - 110, y: crop.midY - 50, width: 220, height: 100)
        var zoneRef: LiveBlurZoneWindow?
        let zone = LiveBlurZoneWindow(contentAppKitRect: rect,
                                      onFrameChanged: { [weak self] _ in self?.syncBlurRects() },
                                      onClose: { [weak self] in
                                          guard let self = self, let z = zoneRef else { return }
                                          z.orderOut(nil)
                                          self.blurWindows.removeAll { $0 === z }
                                          self.syncBlurRects()
                                      })
        zoneRef = zone
        zone.orderFrontRegardless()
        blurWindows.append(zone)
        syncBlurRects()
    }

    @MainActor
    func stopRecording() {
        if isArmed && !isRecording {
            isArmed = false
            teardownUI()
            onStateChange?(.idle)
            return
        }
        guard isRecording else { return }
        teardownUI()

        isRecording = false
        isPaused = false
        onStateChange?(.idle)

        composeTimer?.cancel()
        composeTimer = nil
        audioSession?.stopRunning()
        audioSession = nil

        sessionLock.lock(); basePTS = .zero; sessionLock.unlock()

        Task {
            for d in displays { await d.stop() }
            displays = []

            videoInput?.markAsFinished()
            audioInput?.markAsFinished()
            await assetWriter?.finishWriting()
            let status = assetWriter?.status
            let err = assetWriter?.error
            assetWriter = nil; videoInput = nil; audioInput = nil; pixelBufferAdaptor = nil

            if let fileURL = self.outputFileURL {
                await MainActor.run {
                    if status == .completed {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.writeObjects([fileURL as NSURL])
                        NSWorkspace.shared.activateFileViewerSelecting([fileURL])
                    } else {
                        let alert = NSAlert()
                        alert.messageText = "Запись не сохранилась"
                        alert.informativeText = "\(err.map { "\($0)" } ?? "неизвестная ошибка")"
                        alert.runModal()
                    }
                }
            }
        }
    }

    @MainActor
    func cancelRecording() {
        let file = self.outputFileURL
        let wasRecording = isRecording
        stopRecording()
        if wasRecording, let f = file {
            Task { try? await Task.sleep(nanoseconds: 1_500_000_000); try? FileManager.default.removeItem(at: f) }
        }
    }

    @MainActor
    private func teardownUI() {
        hideFloatingBar()
        liveFrameWindow?.orderOut(nil)
        liveFrameWindow = nil
        clearBlurZones()
    }

    @MainActor
    private func showFloatingBar() {
        let win = NSPanel(contentRect: NSRect(x: -10000, y: -10000, width: 330, height: 46),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        win.isOpaque = false
        win.backgroundColor = .clear
        win.level = .statusBar
        win.isFloatingPanel = true
        win.becomesKeyOnlyIfNeeded = true
        win.isMovableByWindowBackground = false
        win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        let host = NSHostingView(rootView: RecordingControlBarView())
        win.contentView = host
        win.orderFrontRegardless()
        self.floatingBarWindow = win
    }

    /// Панель по центру под нижним краем зоны; если не влезает — над зоной; если и там нет — внутри у нижнего края
    @MainActor
    func placeBar(near zone: CGRect) {
        guard let win = floatingBarWindow else { return }
        let size = (win.contentView as? NSHostingView<RecordingControlBarView>)?.fittingSize ?? win.frame.size
        let w = max(size.width, 60), h = max(size.height, 30)
        let screen = Coord.screen(for: zone)
        let vf = screen.visibleFrame
        var x = zone.midX - w / 2
        x = min(max(x, vf.minX), max(vf.minX, vf.maxX - w))
        var y = zone.minY - h - 8
        if y < vf.minY { y = zone.maxY + 8 }
        if y + h > vf.maxY { y = zone.minY + 8 }
        win.setFrame(NSRect(x: x, y: y, width: w, height: h), display: true)
    }

    @MainActor
    private func hideFloatingBar() {
        floatingBarWindow?.orderOut(nil)
        floatingBarWindow = nil
    }

    @MainActor
    private func showLiveFrame(appKitRect: CGRect) {
        let win = LiveResizableFrameWindow(contentAppKitRect: appKitRect) { [weak self] newRect in
            Task { @MainActor in self?.onFrameChanged(newRect) }
        }
        win.orderFrontRegardless()
        self.liveFrameWindow = win
    }

    @MainActor
    private func clearBlurZones() {
        for w in blurWindows { w.orderOut(nil) }
        blurWindows.removeAll()
        sessionLock.lock(); blurRectsAppKit = []; sessionLock.unlock()
    }
}

// --- Плавающее превью ---
@MainActor
final class FloatingThumbnailManager {
    static let shared = FloatingThumbnailManager()
    private var window: NSWindow?
    private var dismissTimer: Timer?

    func showThumbnail(for image: NSImage, onOpenEditor: @escaping () -> Void) {
        dismiss()
        let screen = Coord.screen(containing: NSEvent.mouseLocation)

        let width: CGFloat = 200
        let height: CGFloat = 130
        let rect = NSRect(x: screen.visibleFrame.maxX - width - 20, y: screen.visibleFrame.minY + 20, width: width, height: height)

        let win = NSWindow(contentRect: rect, styleMask: [.borderless], backing: .buffered, defer: false)
        win.isOpaque = false
        win.backgroundColor = .clear
        win.level = .floating
        win.hasShadow = true

        let view = FloatingThumbnailView(image: image, onOpen: { [weak self] in
            self?.dismiss()
            onOpenEditor()
        }, onClose: { [weak self] in
            self?.dismiss()
        })

        win.contentView = NSHostingView(rootView: view)
        win.makeKeyAndOrderFront(nil)
        self.window = win

        resetTimer()
    }

    func resetTimer() {
        dismissTimer?.invalidate()
        dismissTimer = Timer.scheduledTimer(withTimeInterval: 5.0, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.dismiss() }
        }
    }

    func pauseTimer() { dismissTimer?.invalidate() }

    func dismiss() {
        dismissTimer?.invalidate()
        dismissTimer = nil
        window?.orderOut(nil)
        window = nil
    }
}

struct FloatingThumbnailView: View {
    let image: NSImage
    var onOpen: () -> Void
    var onClose: () -> Void
    @State private var isHovered = false

    var body: some View {
        ZStack(alignment: .topTrailing) {
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(red: 0.12, green: 0.13, blue: 0.15))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.2), lineWidth: 1))

            Image(nsImage: image)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .cornerRadius(6)
                .padding(8)

            if isHovered {
                HStack(spacing: 6) {
                    Button(action: onOpen) {
                        Image(systemName: "pencil.tip.crop.circle").foregroundColor(.white)
                    }
                    Button(action: onClose) {
                        Image(systemName: "xmark.circle.fill").foregroundColor(.gray)
                    }
                }
                .padding(6)
                .buttonStyle(.plain)
            }
        }
        .frame(width: 190, height: 120)
        .onHover { h in
            isHovered = h
            if h { FloatingThumbnailManager.shared.pauseTimer() } else { FloatingThumbnailManager.shared.resetTimer() }
        }
        .onTapGesture { onOpen() }
    }
}

final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

func drawCustomCrosshairCursor(at pt: NSPoint, globalPt: NSPoint, bounds: NSRect, isDragging: Bool, rect: NSRect?) {
    guard let ctx = NSGraphicsContext.current?.cgContext else { return }

    ctx.saveGState()
    ctx.setLineWidth(1.5)
    ctx.setStrokeColor(CGColor(red: 0.2, green: 0.7, blue: 1.0, alpha: 0.9))
    ctx.strokeEllipse(in: CGRect(x: pt.x - 10, y: pt.y - 10, width: 20, height: 20))

    ctx.setLineWidth(1.5)
    ctx.setStrokeColor(CGColor(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.95))
    ctx.move(to: CGPoint(x: pt.x - 22, y: pt.y)); ctx.addLine(to: CGPoint(x: pt.x - 4, y: pt.y))
    ctx.move(to: CGPoint(x: pt.x + 4, y: pt.y)); ctx.addLine(to: CGPoint(x: pt.x + 22, y: pt.y))
    ctx.move(to: CGPoint(x: pt.x, y: pt.y - 22)); ctx.addLine(to: CGPoint(x: pt.x, y: pt.y - 4))
    ctx.move(to: CGPoint(x: pt.x, y: pt.y + 4)); ctx.addLine(to: CGPoint(x: pt.x, y: pt.y + 22))
    ctx.strokePath()

    ctx.setFillColor(CGColor(red: 1.0, green: 0.25, blue: 0.35, alpha: 1.0))
    ctx.fillEllipse(in: CGRect(x: pt.x - 1.5, y: pt.y - 1.5, width: 3, height: 3))
    ctx.restoreGState()

    // Координаты — Quartz-глобальные (верх-лево primary), как на винде физические
    let qY = Int(Coord.primary.frame.maxY - globalPt.y)
    let qX = Int(globalPt.x)

    let text1: String
    let text2: String

    if isDragging, let r = rect {
        text1 = "W: \(Int(r.width))"
        text2 = "H: \(Int(r.height))"
    } else {
        text1 = "\(qX)"
        text2 = "\(qY)"
    }

    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = .center
    let attrs: [NSAttributedString.Key: Any] = [
        .font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .bold),
        .foregroundColor: NSColor.white,
        .paragraphStyle: paragraph
    ]

    let str1 = NSAttributedString(string: text1, attributes: attrs)
    let str2 = NSAttributedString(string: text2, attributes: attrs)
    let maxW = max(str1.size().width, str2.size().width) + 12
    let badgeH: CGFloat = 30

    var badgeX = pt.x + 14
    var badgeY = pt.y - 32
    if badgeX + maxW > bounds.width { badgeX = pt.x - maxW - 14 }
    if badgeY < 10 { badgeY = pt.y + 14 }

    let badgeRect = NSRect(x: badgeX, y: badgeY, width: maxW, height: badgeH)
    NSColor.black.withAlphaComponent(0.8).setFill()
    let badgePath = NSBezierPath(roundedRect: badgeRect, xRadius: 5, yRadius: 5)
    badgePath.fill()
    NSColor.white.withAlphaComponent(0.2).setStroke()
    badgePath.lineWidth = 1
    badgePath.stroke()

    str1.draw(at: NSPoint(x: badgeRect.origin.x + (maxW - str1.size().width) / 2, y: badgeRect.origin.y + 14))
    str2.draw(at: NSPoint(x: badgeRect.origin.x + (maxW - str2.size().width) / 2, y: badgeRect.origin.y + 2))
}

enum OverlayMode { case area, scroll, record, smart }

/// Состояние выделения, общее для оверлеев всех экранов. Все точки — глобальные AppKit
@MainActor
final class SelectionState {
    var start: NSPoint?
    var current: NSPoint?
    var mouse: NSPoint = NSEvent.mouseLocation
    var isDragging = false
    var hovered: WindowTarget?

    var rect: CGRect? {
        guard let s = start, let c = current else { return nil }
        return CGRect(x: min(s.x, c.x), y: min(s.y, c.y), width: abs(s.x - c.x), height: abs(s.y - c.y))
    }
}

/// Оверлей одного экрана. Мышь считается в глобальных координатах — выделение свободно идёт через стык мониторов
final class ScreenOverlayView: NSView {
    let mode: OverlayMode
    let state: SelectionState
    let windows: [WindowTarget]
    var onRect: ((CGRect) -> Void)?
    var onWindow: ((WindowTarget) -> Void)?
    var onCancel: (() -> Void)?
    var redrawAll: (() -> Void)?

    init(frame: NSRect, mode: OverlayMode, state: SelectionState, windows: [WindowTarget]) {
        self.mode = mode; self.state = state; self.windows = windows
        super.init(frame: frame)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
    }

    private func local(_ global: NSPoint) -> NSPoint {
        guard let w = window else { return global }
        return NSPoint(x: global.x - w.frame.minX, y: global.y - w.frame.minY)
    }

    override func mouseMoved(with event: NSEvent) {
        state.mouse = NSEvent.mouseLocation
        if mode == .smart && !state.isDragging { state.hovered = windows.first { $0.appKitFrame.contains(state.mouse) } }
        redrawAll?()
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        let p = NSEvent.mouseLocation
        state.start = p; state.current = p; state.mouse = p; state.isDragging = false
        if mode == .smart { state.hovered = windows.first { $0.appKitFrame.contains(p) } }
        redrawAll?()
    }

    override func mouseDragged(with event: NSEvent) {
        let p = NSEvent.mouseLocation
        state.current = p; state.mouse = p
        if let s = state.start, hypot(p.x - s.x, p.y - s.y) > 5 { state.isDragging = true; if mode == .smart { state.hovered = nil } }
        if mode != .smart { state.isDragging = true }
        redrawAll?()
    }

    override func mouseUp(with event: NSEvent) {
        let p = NSEvent.mouseLocation
        state.current = p
        guard let r = state.rect else { return }
        let big = r.width > 5 && r.height > 5

        if mode == .smart {
            if state.isDragging && big { onRect?(r) }
            else if let win = windows.first(where: { $0.appKitFrame.contains(p) }) { onWindow?(win) }
            else if big { onRect?(r) }
        } else if big { onRect?(r) }

        state.start = nil; state.current = nil; state.isDragging = false
        redrawAll?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onCancel?() }
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(mode == .smart ? 0.32 : 0.35).setFill()
        dirtyRect.fill()

        if mode == .smart, !state.isDragging, let win = state.hovered {
            let wf = win.appKitFrame
            let localRect = NSRect(origin: local(wf.origin), size: wf.size)
            NSColor.systemBlue.withAlphaComponent(0.18).setFill()
            let path = NSBezierPath(roundedRect: localRect, xRadius: 8, yRadius: 8)
            path.fill()
            NSColor.systemBlue.setStroke()
            path.lineWidth = 3.0
            path.stroke()

            let title = "\(win.ownerName) (Клик: захват окна)"
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: 11), .foregroundColor: NSColor.white]
            let str = NSAttributedString(string: title, attributes: attrs)
            let badge = NSRect(x: localRect.origin.x + 8, y: min(localRect.maxY - 24, bounds.height - 28), width: str.size().width + 14, height: 20)
            NSColor.systemBlue.setFill()
            NSBezierPath(roundedRect: badge, xRadius: 4, yRadius: 4).fill()
            str.draw(at: NSPoint(x: badge.origin.x + 7, y: badge.origin.y + 3))
        }

        let mouseLocal = local(state.mouse)
        if state.isDragging, let r = state.rect, r.width > 1, r.height > 1 {
            let lr = NSRect(origin: local(r.origin), size: r.size)
            NSGraphicsContext.current?.cgContext.clear(lr)
            let strokeColor: NSColor = mode == .record ? .systemRed : (mode == .scroll ? .systemPurple : .white)
            strokeColor.setStroke()
            let path = NSBezierPath(rect: lr)
            path.lineWidth = 1.5
            path.stroke()
            if bounds.contains(mouseLocal) { drawCustomCrosshairCursor(at: mouseLocal, globalPt: state.mouse, bounds: bounds, isDragging: true, rect: r) }
            return
        }

        if bounds.contains(mouseLocal) { drawCustomCrosshairCursor(at: mouseLocal, globalPt: state.mouse, bounds: bounds, isDragging: false, rect: nil) }
    }
}

// --- Менеджер оверлеев: по окну на каждый экран, одно общее выделение ---
@MainActor
final class OverlayManager {
    static let shared = OverlayManager()
    private var overlayWindows: [OverlayWindow] = []
    private var views: [ScreenOverlayView] = []
    private init() {}

    var isActive: Bool { !overlayWindows.isEmpty }

    private func show(mode: OverlayMode, onRect: @escaping (CGRect) -> Void, onWindow: ((WindowTarget) -> Void)? = nil) {
        closeOverlay()
        let state = SelectionState()
        let windows = mode == .smart ? WindowDetector.getVisibleWindows() : []
        let cursorScreen = Coord.screen(containing: NSEvent.mouseLocation)

        for screen in NSScreen.screens {
            let window = OverlayWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.isOpaque = false
            window.backgroundColor = .clear
            window.level = .screenSaver
            window.hasShadow = false
            window.acceptsMouseMovedEvents = true
            window.isReleasedWhenClosed = false

            let view = ScreenOverlayView(frame: NSRect(origin: .zero, size: screen.frame.size), mode: mode, state: state, windows: windows)
            view.onRect = { [weak self] r in self?.closeOverlay(); onRect(r) }
            view.onWindow = { [weak self] t in self?.closeOverlay(); onWindow?(t) }
            view.onCancel = { [weak self] in self?.closeOverlay() }
            view.redrawAll = { [weak self] in self?.views.forEach { $0.needsDisplay = true } }
            window.contentView = view

            overlayWindows.append(window)
            views.append(view)
            if screen == cursorScreen { window.makeKeyAndOrderFront(nil); window.makeFirstResponder(view) } else { window.orderFrontRegardless() }
        }
        NSCursor.hide()
        NSApp.activate(ignoringOtherApps: true)
    }

    func showAreaOverlay(isRecordingMode: Bool = false, isScrollMode: Bool = false, onSelected: @escaping (NSImage) -> Void) {
        let mode: OverlayMode = isRecordingMode ? .record : (isScrollMode ? .scroll : .area)
        show(mode: mode, onRect: { akRect in
            if isRecordingMode {
                ScreenRecorder.shared.arm(initialAppKitRect: akRect)
            } else {
                let qRect = Coord.toQuartz(akRect)
                if isScrollMode {
                    ScrollCaptureManager.shared.startScrollingSession(quartzRect: qRect) { stitchedImg in onSelected(stitchedImg) }
                } else {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                        if let img = CaptureEngine.shared.capture(quartzRect: qRect) { onSelected(img) }
                    }
                }
            }
        })
    }

    func showSmartCombinedOverlay(onSelected: @escaping (NSImage) -> Void) {
        show(mode: .smart, onRect: { akRect in
            let qRect = Coord.toQuartz(akRect)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                if let img = CaptureEngine.shared.capture(quartzRect: qRect) { onSelected(img) }
            }
        }, onWindow: { target in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                if let img = CaptureEngine.shared.captureWindow(target: target) { onSelected(img) }
            }
        })
    }

    func closeOverlay() {
        NSCursor.unhide()
        for w in overlayWindows { w.orderOut(nil) }
        overlayWindows.removeAll()
        views.removeAll()
    }
}

// --- Все 11 инструментов рисования ---
enum DrawTool: String, CaseIterable {
    case arrow = "arrow.up.right"
    case rectangle = "square"
    case ellipse = "circle"
    case text = "textformat"
    case bubble = "bubble.left"
    case step = "12.circle"
    case highlighter = "highlighter"
    case blur = "checkerboard.rectangle"
    case ruler = "ruler"
    case pen = "pencil.tip"
    case crop = "crop"

    var title: String {
        switch self {
        case .arrow: return "↗ Стрелка"
        case .rectangle: return "▢ Рамка"
        case .ellipse: return "◯ Эллипс"
        case .text: return "T Текст"
        case .bubble: return "💬 Выноска"
        case .step: return "① Шаг"
        case .highlighter: return "🖍 Маркер"
        case .blur: return "░ Цензура"
        case .ruler: return "📏 Линейка"
        case .pen: return "✏ Карандаш"
        case .crop: return "✂ Обрезка"
        }
    }
}

struct DrawShape: Identifiable {
    let id = UUID()
    var tool: DrawTool
    var points: [CGPoint]
    var color: Color
    var lineWidth: CGFloat
    var stepNumber: Int = 1
    var textValue: String = ""
}

struct TextAnnotation: Identifiable {
    let id = UUID()
    var text: String
    var position: CGPoint
    var color: Color
    var fontSize: CGFloat = 16
}

enum CanvasItem: Identifiable {
    case shape(DrawShape)
    case text(TextAnnotation)

    var id: UUID {
        switch self {
        case .shape(let s): return s.id
        case .text(let t): return t.id
        }
    }
}

struct WindowDragView: NSViewRepresentable {
    func makeNSView(context: Context) -> DragNSView { DragNSView() }
    func updateNSView(_ nsView: DragNSView, context: Context) {}
}

class DragNSView: NSView {
    override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }
}

struct NativeDragDropButton: NSViewRepresentable {
    var getRenderedImage: () -> NSImage?
    var getFormat: () -> String

    func makeNSView(context: Context) -> QScreenDragSourceView {
        let view = QScreenDragSourceView()
        view.getImage = getRenderedImage
        view.getFormat = getFormat
        return view
    }
    func updateNSView(_ nsView: QScreenDragSourceView, context: Context) {
        nsView.getImage = getRenderedImage
        nsView.getFormat = getFormat
    }
}

final class QScreenDragSourceView: NSView, NSDraggingSource {
    var getImage: (() -> NSImage?)?
    var getFormat: (() -> String)?
    private var dragStartLocation: NSPoint?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 5
        layer?.backgroundColor = NSColor.white.withAlphaComponent(0.12).cgColor

        let icon = NSImageView(frame: NSRect(x: 5, y: 4, width: 18, height: 18))
        icon.image = NSImage(systemSymbolName: "hand.draw.fill", accessibilityDescription: "Drag")
        icon.contentTintColor = .white
        addSubview(icon)
    }

    required init?(coder: NSCoder) { fatalError() }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        return [.copy, .generic, .every]
    }

    override func mouseDown(with event: NSEvent) {
        dragStartLocation = event.locationInWindow
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStartLocation else { return }
        let current = event.locationInWindow
        if hypot(current.x - start.x, current.y - start.y) < 3 { return }
        dragStartLocation = nil

        let fmt = getFormat?() ?? FilenameHelper.getDefaultFormat()
        guard let img = getImage?(),
              let (data, ext) = ImageExportHelper.exportData(image: img, format: fmt) else { return }

        let fileName = FilenameHelper.generateFilename(ext: ext)
        let tempDir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("QScreenDrag", isDirectory: true)
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        let fileURL = tempDir.appendingPathComponent(fileName)
        try? data.write(to: fileURL, options: .atomic)

        let pbItem = NSPasteboardItem()
        pbItem.setString(fileURL.absoluteString, forType: .fileURL)
        pbItem.setString(fileURL.absoluteString, forType: NSPasteboard.PasteboardType("public.file-url"))

        if ext == "png" {
            pbItem.setData(data, forType: .png)
            pbItem.setData(data, forType: NSPasteboard.PasteboardType("public.png"))
        } else if ext == "jpg" || ext == "jpeg" {
            pbItem.setData(data, forType: NSPasteboard.PasteboardType("public.jpeg"))
        } else if ext == "heic" {
            pbItem.setData(data, forType: NSPasteboard.PasteboardType(UTType.heic.identifier))
        }
        if let tiff = img.tiffRepresentation {
            pbItem.setData(tiff, forType: .tiff)
        }
        pbItem.setString(fileURL.path, forType: .string)

        let item = NSDraggingItem(pasteboardWriter: pbItem)
        let thumbSize = NSSize(width: 50, height: 35)
        let thumb = NSImage(size: thumbSize)
        thumb.lockFocus()
        img.draw(in: NSRect(origin: .zero, size: thumbSize), from: .zero, operation: .sourceOver, fraction: 0.9)
        thumb.unlockFocus()

        item.setDraggingFrame(NSRect(x: bounds.midX - 25, y: bounds.midY - 17, width: 50, height: 35), contents: thumb)
        beginDraggingSession(with: [item], event: event, source: self)
    }
}

// --- HUD Графический редактор с поддержкой HEIC ---
struct CaptureEditorView: View {
    @State var currentImage: NSImage
    @State private var pixellatedImage: NSImage
    var onClose: () -> Void
    var onPin: (NSImage) -> Void

    init(image: NSImage, onClose: @escaping () -> Void, onPin: @escaping (NSImage) -> Void) {
        self._currentImage = State(initialValue: image)
        self._pixellatedImage = State(initialValue: generatePixellatedImage(from: image))
        self.onClose = onClose
        self.onPin = onPin
        self._currentExportFormat = State(initialValue: FilenameHelper.getDefaultFormat())
    }

    @State private var items: [CanvasItem] = []
    @State private var currentShape: DrawShape?
    @State private var selectedTool: DrawTool = .arrow
    @State private var selectedColor: Color = Color(red: 1.0, green: 0.18, blue: 0.33)
    @State private var strokeWidth: CGFloat = 4.0
    @State private var stepCounter: Int = 1

    @State private var currentExportFormat: String = "png"
    @State private var isBeautifyEnabled = false
    @State private var selectedGradient: GradientPreset = .nebula

    @State private var cropStart: CGPoint?
    @State private var cropCurrent: CGPoint?

    @State private var activeTextPos: CGPoint?
    @State private var activeTextString: String = ""
    @FocusState private var isTextFocused: Bool
    @State private var toastMessage: String?

    let colorPalette: [Color] = [
        Color(red: 1.0, green: 0.18, blue: 0.33),
        Color(red: 0.20, green: 0.70, blue: 1.0),
        Color(red: 0.20, green: 0.80, blue: 0.40),
        Color(red: 1.0, green: 0.80, blue: 0.0),
        Color.white
    ]

    var cropRect: CGRect? {
        guard let s = cropStart, let c = cropCurrent else { return nil }
        return CGRect(x: min(s.x, c.x), y: min(s.y, c.y), width: abs(s.x - c.x), height: abs(s.y - c.y))
    }

    var innerCanvasContent: some View {
        ZStack(alignment: .topLeading) {
            Image(nsImage: currentImage)
                .resizable()
                .frame(width: currentImage.size.width, height: currentImage.size.height)

            ForEach(items.compactMap { item -> (UUID, CGRect)? in
                if case .shape(let s) = item, s.tool == .blur, s.points.count >= 2 {
                    return (s.id, CGRect(x: min(s.points[0].x, s.points[1].x), y: min(s.points[0].y, s.points[1].y), width: abs(s.points[0].x - s.points[1].x), height: abs(s.points[0].y - s.points[1].y)))
                }
                return nil
            }, id: \.0) { _, rect in
                Image(nsImage: pixellatedImage)
                    .resizable()
                    .frame(width: currentImage.size.width, height: currentImage.size.height)
                    .mask(Rectangle().path(in: rect))
                    .overlay(RoundedRectangle(cornerRadius: 3).stroke(Color.white.opacity(0.35), lineWidth: 1).frame(width: rect.width, height: rect.height).position(x: rect.midX, y: rect.midY))
            }

            if let curr = currentShape, curr.tool == .blur, curr.points.count >= 2 {
                let rect = CGRect(x: min(curr.points[0].x, curr.points[1].x), y: min(curr.points[0].y, curr.points[1].y), width: abs(curr.points[0].x - curr.points[1].x), height: abs(curr.points[0].y - curr.points[1].y))
                Image(nsImage: pixellatedImage)
                    .resizable()
                    .frame(width: currentImage.size.width, height: currentImage.size.height)
                    .mask(Rectangle().path(in: rect))
            }

            Canvas { context, _ in
                for item in items {
                    switch item {
                    case .shape(let shape):
                        if shape.tool != .blur { drawShape(shape, in: &context) }
                    case .text(let t):
                        context.draw(Text(t.text).font(.system(size: t.fontSize, weight: .bold)).foregroundColor(t.color), at: t.position, anchor: .topLeading)
                    }
                }
                if let current = currentShape, current.tool != .blur { drawShape(current, in: &context) }
            }
        }
        .frame(width: currentImage.size.width, height: currentImage.size.height)
    }

    var fullRenderView: some View {
        Group {
            if isBeautifyEnabled {
                ZStack {
                    selectedGradient.gradient
                    innerCanvasContent
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.4), radius: 18, x: 0, y: 10)
                        .padding(36)
                }
                .fixedSize()
            } else {
                innerCanvasContent
            }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                WindowDragView().frame(width: 66)

                HStack(spacing: 3) {
                    Button(action: copyToClipboard) {
                        HStack(spacing: 4) {
                            Image(systemName: "checkmark")
                            Text("Готово")
                        }
                        .font(.system(size: 11, weight: .bold))
                        .padding(.horizontal, 8)
                        .frame(height: 26)
                        .background(Color.green)
                        .foregroundColor(.white)
                        .cornerRadius(5)
                    }.help("Скопировать (Cmd+C)")

                    Button(action: { handleSaveAction(format: currentExportFormat) }) {
                        Image(systemName: "square.and.arrow.down").frame(width: 28, height: 26).background(Color.white.opacity(0.12)).cornerRadius(5)
                    }.help("Сохранить как \(currentExportFormat.uppercased()) (Cmd+S)")

                    Menu {
                        Button("PNG (Без потерь, Retina)") { currentExportFormat = "png" }
                        Button("HEIC (Высокая эффективность)") { currentExportFormat = "heic" }
                        Button("JPG (Компактный)") { currentExportFormat = "jpg" }
                        Button("PDF (Документ)") { currentExportFormat = "pdf" }
                    } label: {
                        HStack(spacing: 2) {
                            Text(currentExportFormat.uppercased())
                                .font(.system(size: 10, weight: .bold, design: .monospaced))
                            Image(systemName: "chevron.down")
                                .font(.system(size: 7, weight: .bold))
                        }
                        .foregroundColor(.white)
                        .padding(.horizontal, 5)
                        .frame(height: 26)
                        .background(Color.white.opacity(0.15))
                        .cornerRadius(5)
                    }
                    .menuStyle(.borderlessButton)

                    NativeDragDropButton(getRenderedImage: { renderFinalImage() }, getFormat: { currentExportFormat })
                        .frame(width: 28, height: 26)
                        .help("Drag & Drop (\(currentExportFormat.uppercased()))")

                    Button(action: pinScreenshot) {
                        Image(systemName: "pin").frame(width: 28, height: 26).background(Color.white.opacity(0.12)).cornerRadius(5)
                    }.help("Закрепить поверх окон (Pin)")

                    Button(action: runOCR) {
                        Image(systemName: "doc.text.viewfinder").frame(width: 28, height: 26).background(Color.white.opacity(0.12)).cornerRadius(5)
                    }.help("OCR Распознавание текста")

                    Button(action: undoLastAction) {
                        Image(systemName: "arrow.uturn.backward").frame(width: 28, height: 26).background(Color.white.opacity(0.12)).cornerRadius(5)
                    }.disabled(items.isEmpty && activeTextPos == nil).help("Отменить (Cmd+Z)")
                }

                Divider().frame(height: 18).background(Color.white.opacity(0.2))

                HStack(spacing: 3) {
                    Button {
                        isBeautifyEnabled.toggle()
                    } label: {
                        Image(systemName: "sparkles")
                            .foregroundColor(isBeautifyEnabled ? .yellow : .gray)
                            .frame(width: 28, height: 26)
                            .background(isBeautifyEnabled ? Color.white.opacity(0.2) : Color.clear)
                            .cornerRadius(5)
                    }
                    .help("Красивый фон (Beautify)")

                    if isBeautifyEnabled {
                        Picker("", selection: $selectedGradient) {
                            ForEach(GradientPreset.allCases) { p in Text(p.rawValue).tag(p) }
                        }
                        .frame(width: 85)
                        .labelsHidden()
                    }
                }

                Divider().frame(height: 18).background(Color.white.opacity(0.2))

                HStack(spacing: 2) {
                    ForEach(DrawTool.allCases, id: \.self) { tool in
                        Button {
                            commitActiveText()
                            selectedTool = tool
                        } label: {
                            Image(systemName: tool.rawValue)
                                .foregroundColor(selectedTool == tool ? .white : .gray)
                                .frame(width: 26, height: 26)
                                .background(selectedTool == tool ? Color.blue : Color.clear)
                                .cornerRadius(5)
                        }
                        .help(tool.title)
                    }
                }

                Divider().frame(height: 18).background(Color.white.opacity(0.2))

                HStack(spacing: 2) {
                    ForEach([2.0, 4.0, 8.0], id: \.self) { width in
                        Button { strokeWidth = CGFloat(width) } label: {
                            Circle().fill(strokeWidth == CGFloat(width) ? Color.white : Color.gray.opacity(0.5)).frame(width: CGFloat(width + 3), height: CGFloat(width + 3)).frame(width: 16, height: 24)
                        }
                    }
                }

                Divider().frame(height: 18).background(Color.white.opacity(0.2))

                HStack(spacing: 4) {
                    ForEach(colorPalette, id: \.self) { color in
                        Button { selectedColor = color } label: {
                            Circle()
                                .fill(color)
                                .frame(width: 13, height: 13)
                                .overlay(Circle().stroke(selectedColor == color ? Color.white : Color.clear, lineWidth: 1.5))
                        }
                    }
                    ColorPicker("", selection: $selectedColor).labelsHidden().frame(width: 18, height: 18)
                }

                Spacer()
                WindowDragView().frame(width: 40)
            }
            .buttonStyle(.plain)
            .padding(.horizontal, 8)
            .padding(.vertical, 7)
            .frame(height: 44)
            .background(Color(red: 0.12, green: 0.13, blue: 0.15))

            ZStack {
                Color(red: 0.08, green: 0.09, blue: 0.10)

                ZStack(alignment: .topLeading) {
                    fullRenderView

                    if let rect = cropRect, selectedTool == .crop {
                        ZStack(alignment: .top) {
                            Path { $0.addRect(rect) }.stroke(Color.white, style: StrokeStyle(lineWidth: 2, dash: [5, 5]))
                            Button(action: { applyCrop(rect) }) {
                                HStack(spacing: 4) { Image(systemName: "crop"); Text("Обрезать") }
                                    .font(.system(size: 11, weight: .bold)).padding(.horizontal, 8).padding(.vertical, 4).background(Color.blue).foregroundColor(.white).cornerRadius(5)
                            }.buttonStyle(.plain).offset(y: -26)
                        }
                    }

                    if let pos = activeTextPos {
                        TextField("", text: $activeTextString)
                            .textFieldStyle(.plain).font(.system(size: 18, weight: .bold)).foregroundColor(selectedColor)
                            .padding(.horizontal, 6).padding(.vertical, 4).background(Color.black.opacity(0.85).cornerRadius(4))
                            .overlay(RoundedRectangle(cornerRadius: 4).stroke(selectedColor, lineWidth: 1.5))
                            .fixedSize().offset(x: max(0, min(pos.x, currentImage.size.width - 60)), y: max(0, min(pos.y, currentImage.size.height - 30)))
                            .focused($isTextFocused).onSubmit { commitActiveText() }
                    }
                }
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { val in
                            if selectedTool == .text { return }
                            if selectedTool == .crop {
                                if cropStart == nil { cropStart = val.startLocation }
                                cropCurrent = val.location
                                return
                            }
                            if currentShape == nil {
                                var shape = DrawShape(tool: selectedTool, points: [val.startLocation, val.location], color: selectedColor, lineWidth: strokeWidth)
                                if selectedTool == .step { shape.stepNumber = stepCounter; stepCounter += 1 }
                                currentShape = shape
                            } else {
                                if selectedTool == .pen || selectedTool == .highlighter {
                                    if let last = currentShape?.points.last, hypot(val.location.x - last.x, val.location.y - last.y) > 3 { currentShape?.points.append(val.location) }
                                } else { currentShape?.points[1] = val.location }
                            }
                        }
                        .onEnded { val in
                            if selectedTool == .crop { return }
                            if selectedTool == .text {
                                commitActiveText()
                                activeTextPos = val.location
                                activeTextString = ""
                                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { isTextFocused = true }
                                return
                            }
                            if let shape = currentShape { items.append(.shape(shape)); currentShape = nil }
                        }
                )

                if let toast = toastMessage {
                    Text(toast).font(.system(size: 13, weight: .semibold)).foregroundColor(.white).padding(.horizontal, 14).padding(.vertical, 8).background(Color.black.opacity(0.85)).cornerRadius(8).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom).padding(.bottom, 20)
                }
            }
        }
        .frame(minWidth: 800, minHeight: 46 + currentImage.size.height + (isBeautifyEnabled ? 72 : 0))
    }

    private func showToast(_ text: String) {
        withAnimation { toastMessage = text }
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            withAnimation { toastMessage = nil }
        }
    }

    private func applyCrop(_ rect: CGRect) {
        guard let finalImg = renderFinalImage(),
              let tiff = finalImg.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff) else { return }
        let scale = finalImg.pixelScale
        let cropPixelRect = CGRect(x: rect.origin.x * scale, y: (currentImage.size.height - rect.origin.y - rect.height) * scale, width: rect.width * scale, height: rect.height * scale)
        guard let cgImg = bitmap.cgImage?.cropping(to: cropPixelRect) else { return }
        let cropped = NSImage(cgImage: cgImg, size: rect.size)
        currentImage = cropped
        pixellatedImage = generatePixellatedImage(from: cropped)
        items.removeAll(); cropStart = nil; cropCurrent = nil; selectedTool = .arrow
    }

    private func runOCR() {
        if let finalImg = renderFinalImage() {
            let recognized = OCREngine.extractText(from: finalImg)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(recognized, forType: .string)
            showToast(recognized.isEmpty ? "Текст не найден" : "Текст скопирован!")
        }
    }

    private func commitActiveText() {
        if let pos = activeTextPos {
            let clean = activeTextString.trimmingCharacters(in: .whitespacesAndNewlines)
            if !clean.isEmpty { items.append(.text(TextAnnotation(text: clean, position: CGPoint(x: pos.x + 6, y: pos.y + 4), color: selectedColor))) }
            activeTextPos = nil; activeTextString = ""; isTextFocused = false
        }
    }

    private func undoLastAction() {
        if activeTextPos != nil { activeTextPos = nil; activeTextString = ""; return }
        if !items.isEmpty { items.removeLast() }
    }

    private func drawShape(_ shape: DrawShape, in context: inout GraphicsContext) {
        var path = Path()
        switch shape.tool {
        case .rectangle:
            if shape.points.count >= 2 {
                let rect = CGRect(x: min(shape.points[0].x, shape.points[1].x), y: min(shape.points[0].y, shape.points[1].y), width: abs(shape.points[1].x - shape.points[0].x), height: abs(shape.points[1].y - shape.points[0].y))
                path.addRoundedRect(in: rect, cornerSize: CGSize(width: 4, height: 4))
                context.stroke(path, with: .color(shape.color), lineWidth: shape.lineWidth)
            }
        case .ellipse:
            if shape.points.count >= 2 {
                let rect = CGRect(x: min(shape.points[0].x, shape.points[1].x), y: min(shape.points[0].y, shape.points[1].y), width: abs(shape.points[1].x - shape.points[0].x), height: abs(shape.points[1].y - shape.points[0].y))
                path.addEllipse(in: rect)
                context.stroke(path, with: .color(shape.color), lineWidth: shape.lineWidth)
            }
        case .arrow:
            if shape.points.count >= 2 {
                let start = shape.points[0]; let end = shape.points[1]
                path.move(to: start); path.addLine(to: end)
                let angle = atan2(end.y - start.y, end.x - start.x)
                let len: CGFloat = 16
                path.move(to: end); path.addLine(to: CGPoint(x: end.x - len * cos(angle - .pi/6), y: end.y - len * sin(angle - .pi/6)))
                path.move(to: end); path.addLine(to: CGPoint(x: end.x - len * cos(angle + .pi/6), y: end.y - len * sin(angle + .pi/6)))
                context.stroke(path, with: .color(shape.color), lineWidth: shape.lineWidth)
            }
        case .bubble:
            if shape.points.count >= 2 {
                let rect = CGRect(x: min(shape.points[0].x, shape.points[1].x), y: min(shape.points[0].y, shape.points[1].y), width: max(80, abs(shape.points[1].x - shape.points[0].x)), height: max(40, abs(shape.points[1].y - shape.points[0].y)))
                path.addRoundedRect(in: rect, cornerSize: CGSize(width: 10, height: 10))
                context.fill(path, with: .color(Color.black.opacity(0.85)))
                context.stroke(path, with: .color(shape.color), lineWidth: shape.lineWidth)
            }
        case .highlighter:
            path.addLines(shape.points)
            context.stroke(path, with: .color(shape.color.opacity(0.4)), style: StrokeStyle(lineWidth: 18, lineCap: .square, lineJoin: .round))
        case .ruler:
            if shape.points.count >= 2 {
                let start = shape.points[0]
                let end = shape.points[1]
                let dx = abs(end.x - start.x)
                let dy = abs(end.y - start.y)
                let dist = hypot(end.x - start.x, end.y - start.y)

                var line = Path()
                line.move(to: start); line.addLine(to: end)
                context.stroke(line, with: .color(shape.color), lineWidth: shape.lineWidth)

                let angle = atan2(end.y - start.y, end.x - start.x)
                let capLen: CGFloat = 8
                let perp = angle + .pi / 2

                var caps = Path()
                caps.move(to: CGPoint(x: start.x + capLen * cos(perp), y: start.y + capLen * sin(perp)))
                caps.addLine(to: CGPoint(x: start.x - capLen * cos(perp), y: start.y - capLen * sin(perp)))
                caps.move(to: CGPoint(x: end.x + capLen * cos(perp), y: end.y + capLen * sin(perp)))
                caps.addLine(to: CGPoint(x: end.x - capLen * cos(perp), y: end.y - capLen * sin(perp)))
                context.stroke(caps, with: .color(shape.color), lineWidth: shape.lineWidth)

                if dx > 12 && dy > 12 {
                    var box = Path()
                    box.move(to: start)
                    box.addLine(to: CGPoint(x: end.x, y: start.y))
                    box.addLine(to: end)
                    context.stroke(box, with: .color(shape.color.opacity(0.4)), style: StrokeStyle(lineWidth: 1, dash: [4, 4]))
                }

                let labelText = dx > 8 && dy > 8 ? "\(Int(dist))px (W:\(Int(dx)) H:\(Int(dy)))" : "\(Int(dist))px"
                let midPoint = CGPoint(x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 - 12)
                context.draw(
                    Text(labelText).font(.system(size: 10, weight: .bold, design: .monospaced)).foregroundColor(.white),
                    at: midPoint,
                    anchor: .center
                )
            }
        case .step:
            if let center = shape.points.first {
                context.fill(Path(ellipseIn: CGRect(x: center.x - 13, y: center.y - 13, width: 26, height: 26)), with: .color(shape.color))
                context.draw(Text("\(shape.stepNumber)").font(.system(size: 13, weight: .bold)).foregroundColor(.white), at: center, anchor: .center)
            }
        case .pen:
            path.addLines(shape.points)
            context.stroke(path, with: .color(shape.color), style: StrokeStyle(lineWidth: shape.lineWidth, lineCap: .round, lineJoin: .round))
        default: break
        }
    }

    @MainActor
    private func renderFinalImage() -> NSImage? {
        commitActiveText()
        let renderer = ImageRenderer(content: fullRenderView)
        renderer.scale = currentImage.pixelScale
        if let img = renderer.nsImage {
            return img
        }
        return currentImage
    }

    @MainActor
    private func copyToClipboard() {
        if let finalImage = renderFinalImage() {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([finalImage])
            onClose()
        }
    }

    @MainActor
    private func pinScreenshot() {
        if let finalImage = renderFinalImage() { onPin(finalImage) }
    }

    @MainActor
    private func handleSaveAction(format: String? = nil) {
        let fmt = format ?? currentExportFormat
        let directSave = UserDefaults.standard.bool(forKey: "directSaveEnabled")
        if directSave {
            saveDirectlyToFolder(format: fmt)
        } else {
            saveWithDialog(format: fmt)
        }
    }

    @MainActor
    private func saveDirectlyToFolder(format: String) {
        guard let finalImage = renderFinalImage(),
              let (data, ext) = ImageExportHelper.exportData(image: finalImage, format: format) else { return }

        let folder = FilenameHelper.getDefaultSaveFolder()
        let fileName = FilenameHelper.generateFilename(ext: ext)
        let targetURL = folder.appendingPathComponent(fileName)

        do {
            try data.write(to: targetURL, options: .atomic)
            showToast("Сохранено (\(ext.uppercased())): \(folder.lastPathComponent)/\(fileName)")
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { self.onClose() }
        } catch {
            saveWithDialog(format: format)
        }
    }

    @MainActor
    private func saveWithDialog(format: String) {
        guard let finalImage = renderFinalImage() else { return }

        let savePanel = NSSavePanel()
        savePanel.allowedContentTypes = [.png, .jpeg, .pdf, .heic]
        savePanel.canCreateDirectories = true
        savePanel.directoryURL = FilenameHelper.getDefaultSaveFolder()
        savePanel.nameFieldStringValue = FilenameHelper.generateFilename(ext: format)

        savePanel.begin { result in
            if result == .OK, let targetURL = savePanel.url {
                let chosenExt = targetURL.pathExtension.lowercased()
                if let (data, _) = ImageExportHelper.exportData(image: finalImage, format: chosenExt) {
                    try? data.write(to: targetURL, options: .atomic)
                    DispatchQueue.main.async { self.onClose() }
                }
            }
        }
    }
}

// --- Окно настроек ---
struct SettingsWindowView: View {
    @State private var launchAtLogin = (SMAppService.mainApp.status == .enabled)
    @AppStorage("showThumbnail") private var showThumbnail = false
    @AppStorage("defaultImageFormat") private var defaultImageFormat = "png"
    @AppStorage("jpegQuality") private var jpegQuality = 0.85
    @AppStorage("filenamePrefix") private var filenamePrefix = "QScreen"
    @AppStorage("filenameDateFormat") private var filenameDateFormat = "dd.MM.yyyy_HH.mm.ss"
    @AppStorage("defaultSaveFolderPath") private var defaultSaveFolderPath = ""
    @AppStorage("directSaveEnabled") private var directSaveEnabled = false

    @AppStorage("videoFormat") private var videoFormat = "mp4"
    @AppStorage("videoCodec") private var videoCodec = "hevc"
    @AppStorage("videoFPS") private var videoFPS = 60
    @AppStorage("videoRecordAudio") private var videoRecordAudio = false
    @AppStorage("videoShowCursor") private var videoShowCursor = true

    var currentFolderDisplay: String {
        if defaultSaveFolderPath.isEmpty { return "Рабочий стол (Desktop)" }
        return (defaultSaveFolderPath as NSString).lastPathComponent
    }

    var previewName: String {
        let formatter = DateFormatter()
        let datePart: String
        if filenameDateFormat == "unix" {
            datePart = "\(Int(Date().timeIntervalSince1970))"
        } else {
            formatter.dateFormat = filenameDateFormat
            datePart = formatter.string(from: Date())
        }
        let cleanPrefix = filenamePrefix.trimmingCharacters(in: .whitespacesAndNewlines)
        let base = cleanPrefix.isEmpty ? datePart : "\(cleanPrefix)_\(datePart)"
        let ext = defaultImageFormat.lowercased() == "jpeg" ? "jpg" : defaultImageFormat.lowercased()
        return "\(base).\(ext)"
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Горячие клавиши")
                    .font(.system(size: 13, weight: .bold))

                Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
                    GridRow {
                        Text("Захват области:")
                        KeyboardShortcuts.Recorder(for: .captureArea)
                    }
                    GridRow {
                        Text("Умный захват (Окно / Зона):")
                        KeyboardShortcuts.Recorder(for: .captureSmart)
                    }
                    GridRow {
                        Text("Скролл-скриншот:")
                        KeyboardShortcuts.Recorder(for: .captureScroll)
                    }
                    GridRow {
                        Text("Весь экран:")
                        KeyboardShortcuts.Recorder(for: .captureScreen)
                    }
                    GridRow {
                        Text("Запись видео (Зона → Старт → Стоп):")
                        KeyboardShortcuts.Recorder(for: .recordArea)
                    }
                    GridRow {
                        Text("Остановить запись видео:")
                        KeyboardShortcuts.Recorder(for: .recordStop)
                    }
                    GridRow {
                        Text("Пауза / Продолжить запись:")
                        KeyboardShortcuts.Recorder(for: .recordPause)
                    }
                }

                Divider()

                Text("Формат и имя сохраняемых файлов")
                    .font(.system(size: 13, weight: .bold))

                VStack(alignment: .leading, spacing: 7) {
                    HStack(spacing: 12) {
                        Text("Формат по умолчанию:")
                            .frame(width: 140, alignment: .leading)
                        Picker("", selection: $defaultImageFormat) {
                            Text("PNG (Без потерь, Retina)").tag("png")
                            Text("HEIC (Высокая эффективность)").tag("heic")
                            Text("JPG (Компактный размер)").tag("jpg")
                            Text("PDF (Документ)").tag("pdf")
                        }
                        .frame(width: 220)
                        .labelsHidden()
                    }

                    if defaultImageFormat == "jpg" || defaultImageFormat == "heic" {
                        HStack(spacing: 12) {
                            Text("Качество сжатия:")
                                .frame(width: 140, alignment: .leading)
                            Slider(value: $jpegQuality, in: 0.5...1.0, step: 0.05)
                                .frame(width: 160)
                            Text("\(Int(jpegQuality * 100))%")
                                .font(.system(size: 11, weight: .bold, design: .monospaced))
                                .foregroundColor(.blue)
                        }
                    }

                    HStack(spacing: 12) {
                        Text("Префикс:")
                            .frame(width: 140, alignment: .leading)
                        TextField("QScreen", text: $filenamePrefix)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 220)
                    }

                    HStack(spacing: 12) {
                        Text("Формат даты:")
                            .frame(width: 140, alignment: .leading)
                        Picker("", selection: $filenameDateFormat) {
                            Text("ДД.ММ.ГГГГ_ЧЧ.мм.сс").tag("dd.MM.yyyy_HH.mm.ss")
                            Text("ГГГГ-ММ-ДД_ЧЧ-мм-сс").tag("yyyy-MM-dd_HH-mm-ss")
                            Text("ГГГГММДД_ЧЧммсс").tag("yyyyMMdd_HHmmss")
                            Text("ДД-ММ-ГГГГ_ЧЧ-мм-сс").tag("dd-MM-yyyy_HH-mm-ss")
                            Text("Unix Timestamp").tag("unix")
                        }
                        .frame(width: 220)
                        .labelsHidden()
                    }

                    HStack(spacing: 6) {
                        Text("Пример имени:")
                            .font(.system(size: 11))
                            .foregroundColor(.secondary)
                        Text(previewName)
                            .font(.system(size: 11, weight: .semibold, design: .monospaced))
                            .foregroundColor(.blue)
                    }
                }

                Divider()

                Text("Запись видео")
                    .font(.system(size: 13, weight: .bold))

                VStack(alignment: .leading, spacing: 7) {
                    HStack(spacing: 12) {
                        Text("Видеокодек:")
                            .frame(width: 140, alignment: .leading)
                        Picker("", selection: $videoCodec) {
                            Text("H.265 / HEVC (Высокая эффективность)").tag("hevc")
                            Text("H.264 (Макс. совместимость)").tag("h264")
                        }
                        .frame(width: 220)
                        .labelsHidden()
                    }

                    HStack(spacing: 12) {
                        Text("Контейнер:")
                            .frame(width: 140, alignment: .leading)
                        Picker("", selection: $videoFormat) {
                            Text("MP4").tag("mp4")
                            Text("MOV").tag("mov")
                        }
                        .frame(width: 220)
                        .labelsHidden()
                    }

                    HStack(spacing: 12) {
                        Text("Частота кадров:")
                            .frame(width: 140, alignment: .leading)
                        Picker("", selection: $videoFPS) {
                            Text("60 кадров/сек (Плавное)").tag(60)
                            Text("30 кадров/сек (Компактное)").tag(30)
                        }
                        .frame(width: 220)
                        .labelsHidden()
                    }

                    Toggle("Записывать звук с микрофона", isOn: $videoRecordAudio)
                    Toggle("Показывать курсор и клики мыши", isOn: $videoShowCursor)
                }

                Divider()

                Text("Папка для сохранения")
                    .font(.system(size: 13, weight: .bold))

                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 12) {
                        Text("Папка:")
                            .frame(width: 140, alignment: .leading)
                        Text(currentFolderDisplay)
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(.white)
                            .frame(width: 150, alignment: .leading)
                            .lineLimit(1)
                        Button("Выбрать...") {
                            selectFolder()
                        }
                    }
                    Toggle("Сохранять сразу в папку (без диалогового окна)", isOn: $directSaveEnabled)
                }

                Divider()

                Text("Действия и система")
                    .font(.system(size: 13, weight: .bold))

                Toggle("Миниатюра в углу вместо редактора (клик по ней открывает редактор)", isOn: $showThumbnail)
                Toggle("Запуск при входе в macOS", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { val in
                        do {
                            if val { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                        } catch { print("SMAppService: \(error)") }
                    }

                Button(action: {
                    UpdateChecker.checkForUpdates(isUserInitiated: true)
                }) {
                    HStack(spacing: 4) {
                        Image(systemName: "arrow.triangle.2.circlepath")
                        Text("Проверить обновления...")
                    }
                }
                .padding(.top, 4)

                Divider()

                HStack {
                    Spacer()
                    Text("QScreen v\(UpdateChecker.currentVersion) (ScreenCaptureKit Core)")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(.secondary)
                    Spacer()
                }
            }
            .padding(18)
            .frame(width: 510)
        }
        .frame(height: 580)
    }

    private func selectFolder() {
        let openPanel = NSOpenPanel()
        openPanel.canChooseFiles = false
        openPanel.canChooseDirectories = true
        openPanel.allowsMultipleSelection = false
        openPanel.prompt = "Выбрать"
        if openPanel.runModal() == .OK, let url = openPanel.url {
            defaultSaveFolderPath = url.path
        }
    }
}

// --- AppKit Application Delegate ---
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    static weak var shared: AppDelegate?
    private var statusItem: NSStatusItem!
    private var editorWindow: NSWindow?
    private var settingsWindow: NSWindow?
    private var pinnedWindows: [NSWindow] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppDelegate.shared = self
        NSApplication.shared.setActivationPolicy(.regular)
        setupMenuBar()
        setupHotkeys()
        if !CGPreflightScreenCaptureAccess() { CGRequestScreenCaptureAccess() } // без этого скриншоты — просто обои
        UpdateChecker.checkForUpdates(isUserInitiated: false)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { captureAreaAction() }
        return true
    }

    private func setupMenuBar() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        updateMenuBar(state: .idle)

        ScreenRecorder.shared.onStateChange = { [weak self] state in
            self?.updateMenuBar(state: state)
        }
    }

    private func updateMenuBar(state: RecorderState) {
        if let button = statusItem.button {
            let name: String
            switch state {
            case .recording: name = "record.circle.fill"
            case .armed: name = "record.circle"
            case .idle: name = "camera.viewfinder"
            }
            button.image = NSImage(systemSymbolName: name, accessibilityDescription: "QScreen")
            button.contentTintColor = state == .idle ? nil : .red
        }

        let menu = NSMenu()
        switch state {
        case .recording:
            menu.addItem(NSMenuItem(title: "Остановить запись", action: #selector(stopRecordingAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Пауза / Продолжить", action: #selector(togglePauseAction), keyEquivalent: ""))
        case .armed:
            menu.addItem(NSMenuItem(title: "Начать запись", action: #selector(recordAreaAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Отменить", action: #selector(stopRecordingAction), keyEquivalent: ""))
        case .idle:
            menu.addItem(NSMenuItem(title: "Захват области (Cmd+Shift+4)", action: #selector(captureAreaAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Умный захват (Окно / Зона) (Cmd+Shift+6)", action: #selector(captureSmartAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Скролл-скриншот (Cmd+Shift+7)", action: #selector(captureScrollAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Захват экрана (Cmd+Shift+3)", action: #selector(captureScreenAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Запись видео области (Cmd+Shift+5)", action: #selector(recordAreaAction), keyEquivalent: ""))
        }
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Настройки...", action: #selector(openSettingsAction), keyEquivalent: ","))
        menu.addItem(NSMenuItem(title: "Проверить обновления...", action: #selector(checkUpdatesAction), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Выход", action: #selector(quitAction), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    private func setupHotkeys() {
        KeyboardShortcuts.onKeyUp(for: .captureArea) { [weak self] in self?.captureAreaAction() }
        KeyboardShortcuts.onKeyUp(for: .captureSmart) { [weak self] in self?.captureSmartAction() }
        KeyboardShortcuts.onKeyUp(for: .captureScroll) { [weak self] in self?.captureScrollAction() }
        KeyboardShortcuts.onKeyUp(for: .captureScreen) { [weak self] in self?.captureScreenAction() }
        KeyboardShortcuts.onKeyUp(for: .recordArea) { [weak self] in self?.recordAreaAction() }
        KeyboardShortcuts.onKeyUp(for: .recordStop) { [weak self] in self?.stopRecordingAction() }
        KeyboardShortcuts.onKeyUp(for: .recordPause) { [weak self] in self?.togglePauseAction() }
    }

    /// Хоткей всегда сбрасывает незавершённый оверлей/скролл-сессию/миниатюру — застрявшее окно не должно блокировать всё
    private func resetPending() {
        OverlayManager.shared.closeOverlay()
        ScrollCaptureManager.shared.cancelSession()
        FloatingThumbnailManager.shared.dismiss()
    }

    @objc func captureAreaAction() {
        resetPending()
        OverlayManager.shared.showAreaOverlay(isRecordingMode: false, isScrollMode: false) { [weak self] img in
            self?.handleCapturedImage(img)
        }
    }

    @objc func captureScrollAction() {
        resetPending()
        OverlayManager.shared.showAreaOverlay(isRecordingMode: false, isScrollMode: true) { [weak self] img in
            self?.handleCapturedImage(img)
        }
    }

    @objc func captureSmartAction() {
        resetPending()
        OverlayManager.shared.showSmartCombinedOverlay { [weak self] img in
            self?.handleCapturedImage(img)
        }
    }

    private func handleCapturedImage(_ img: NSImage) {
        let showThumb = UserDefaults.standard.bool(forKey: "showThumbnail")
        if showThumb {
            FloatingThumbnailManager.shared.showThumbnail(for: img) { [weak self] in self?.openEditor(img) }
        } else {
            openEditor(img)
        }
    }

    /// Один хоткей: зона → старт → стоп
    @objc func recordAreaAction() {
        let rec = ScreenRecorder.shared
        if rec.isRecording { rec.stopRecording(); return }
        if rec.isArmed { rec.beginCapture(); return }
        resetPending()
        OverlayManager.shared.showAreaOverlay(isRecordingMode: true, isScrollMode: false) { _ in }
    }

    @objc func stopRecordingAction() {
        ScreenRecorder.shared.stopRecording()
    }

    @objc func togglePauseAction() {
        ScreenRecorder.shared.togglePause()
    }

    @objc func captureScreenAction() {
        resetPending()
        if let img = CaptureEngine.shared.captureFullScreen() { openEditor(img) }
    }

    @objc func openSettingsAction() {
        if settingsWindow == nil {
            let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 510, height: 580), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            win.title = "Настройки"
            win.center()
            win.contentView = NSHostingView(rootView: SettingsWindowView())
            win.isReleasedWhenClosed = false
            settingsWindow = win
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func closeSettings() {
        settingsWindow?.orderOut(nil)
    }

    @objc func checkUpdatesAction() {
        UpdateChecker.checkForUpdates(isUserInitiated: true)
    }

    func openEditor(_ image: NSImage) {
        closeEditor()
        let win = NSWindow(contentRect: NSRect(x: 100, y: 100, width: max(image.size.width + 40, 800), height: image.size.height + 60), styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        win.minSize = NSSize(width: 800, height: 350)
        win.titlebarAppearsTransparent = true
        win.titleVisibility = .hidden
        win.backgroundColor = NSColor(red: 0.12, green: 0.13, blue: 0.15, alpha: 1.0)
        win.center()
        win.isReleasedWhenClosed = false
        win.isMovableByWindowBackground = false
        win.delegate = self

        win.contentView = NSHostingView(rootView: CaptureEditorView(image: image, onClose: { [weak self] in self?.closeEditor() }, onPin: { [weak self] pinned in
            self?.closeEditor()
            self?.createPinnedWindow(pinned)
        }))

        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.editorWindow = win
    }

    func createPinnedWindow(_ image: NSImage) {
        let win = NSWindow(contentRect: NSRect(x: 200, y: 200, width: image.size.width, height: image.size.height), styleMask: [.borderless, .resizable], backing: .buffered, defer: false)
        win.isOpaque = false; win.backgroundColor = .clear; win.level = .floating; win.isMovableByWindowBackground = true
        win.contentView = NSHostingView(rootView: Image(nsImage: image).resizable().aspectRatio(contentMode: .fit).shadow(radius: 8).onTapGesture(count: 2) { win.close() })
        win.makeKeyAndOrderFront(nil)
        pinnedWindows.append(win)
    }

    func closeEditor() {
        editorWindow?.orderOut(nil)
        editorWindow = nil
    }

    func windowWillClose(_ notification: Notification) {
        if let closedWin = notification.object as? NSWindow, closedWin === editorWindow { closeEditor() }
    }

    @objc func quitAction() { NSApplication.shared.terminate(nil) }
}

@main
struct AppMain {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
