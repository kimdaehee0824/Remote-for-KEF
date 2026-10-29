import Testing
@testable import KefW2Driver

struct KefShapeTests {
    private func points(_ figure: KefFigure) -> [KefPoint] { [figure.start] + figure.curves }

    @Test func everyKnownModelHasItsOwnDrawing() {
        let drawings = KefModels.names.map { KefShape.figures(for: $0) }
        for (index, drawing) in drawings.enumerated() {
            #expect(drawing != KefShape.generic)
            #expect(drawings.firstIndex(of: drawing) == index)
        }
    }

    @Test func otherModelsGetTheAppIconsDrawing() {
        #expect(KefShape.figures(for: nil) == KefShape.generic)
        #expect(KefShape.figures(for: "KEF LS30") == KefShape.generic)
    }

    @Test func figuresAreClosedBezierPathsOnTheGrid() {
        for figure in KefModels.names.flatMap({ KefShape.figures(for: $0) }) + KefShape.generic {
            #expect(figure.curves.count % 3 == 0)
            #expect(figure.curves.last == figure.start)
            #expect(points(figure).allSatisfy { (0...48).contains($0.x) && (0...48).contains($0.y) })
        }
    }

    @Test func edgeSitsInsideTheCabinet() throws {
        for model in KefModels.names + ["generic"] {
            let figures = KefShape.figures(for: model)
            let edges = figures.filter(\.stroked)
            #expect(edges.count == 1 && edges[0].paint == .edge)
            let index = try #require(figures.firstIndex(of: edges[0]))
            let cabinet = figures[index - 1]
            let left = points(edges[0]).map(\.x).min()! - points(cabinet).map(\.x).min()!
            let bottom = points(cabinet).map(\.y).max()! - points(edges[0]).map(\.y).max()!
            #expect((0.74...0.76).contains(left) && (0.74...0.76).contains(bottom), "\(model)")
        }
    }

    @Test func uniQDriverIsWhereTheModelHasIt() throws {
        let drivers: [(model: String, cx: Double, cy: Double, radius: Double)] = [
            ("LSX II LT", 24, 24, 10), ("LS50 Wireless II", 24, 23.25, 9), ("LS LUXE", 24, 21.95, 9.4), ("LS60 Wireless", 24, 13.4, 4.6),
        ]
        for driver in drivers {
            let figures = KefShape.figures(for: driver.model)
            let trim = try #require(figures.last { $0.paint == .trim })
            #expect(abs(points(trim).map(\.x).max()! - (driver.cx + driver.radius)) < 1e-6)
            #expect(abs(points(trim).map(\.y).min()! - (driver.cy - driver.radius)) < 1e-6)
            #expect(figures.last?.paint == .tweeter)
        }
    }

    @Test func luxeIsSymmetric() throws {
        let body = try #require(KefShape.figures(for: "LS LUXE").first { $0.paint == .cabinet })
        let xs = Set(points(body).map { (($0.x - 24) * 100).rounded() })
        #expect(xs.allSatisfy { xs.contains(-$0) || $0 == 0 })
    }

    @Test func reportedModelWinsOverThePickedOne() {
        #expect(KefModels.shown(reported: "LSX II LT", picked: "LS50 Wireless II") == "LSX II LT")
        #expect(KefModels.shown(reported: "KEF SP4099", picked: "LS50 Wireless II") == "LS50 Wireless II")
        #expect(KefModels.shown(reported: "KEF SP4099", picked: nil) == "KEF SP4099")
        #expect(KefModels.shown(reported: "KEF SP4099", picked: "KEF LS30") == "KEF SP4099")
        #expect(KefModels.shown(reported: nil, picked: "LS LUXE") == "LS LUXE")
        #expect(KefModels.shown(reported: nil, picked: nil) == nil)
    }
}
