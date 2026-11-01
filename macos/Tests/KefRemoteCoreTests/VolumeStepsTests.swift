import Testing
@testable import KefRemoteCore

/// Same cases as the Windows DesktopLogicTests and VolumeKeyAccumulatorTests.
struct VolumeStepsTests {
    @Test(arguments: [
        (0, 1, 12), (12, 1, 15), (13, 1, 15), (59, 1, 65), (65, 1, 69), (98, 1, 100),
        (15, -1, 12), (12, -1, 0), (5, -1, 0), (70, -1, 66), (67, -1, 65),
    ])
    func perceptualSteps(_ current: Int, _ direction: Int, _ expected: Int) {
        #expect(VolumeSteps.nextPerceptual(current, direction: direction) == expected)
    }

    @Test func perceptualStepRespectsTheLimit() {
        #expect(VolumeSteps.next(current: 29, direction: 1, step: VolumeSteps.perceptual, limit: 30) == 30)
        #expect(VolumeSteps.next(current: 29, direction: -1, step: VolumeSteps.perceptual, limit: 30) == 25)
    }

    @Test(arguments: [
        (20, 1, 1, 30, 21),
        (29, 1, 2, 30, 30), // the step stops at the limit
        (30, 1, 1, 30, 30), // at the limit: not raised
        (45, 1, 1, 30, 45), // above it: never raised, never forced down
        (45, -1, 1, 30, 44), // above it: lowering still works
        (1, -1, 2, 30, 0),
        (0, -1, 1, 30, 0),
    ])
    func fixedStepsStopAtTheLimitAndZero(_ current: Int, _ direction: Int, _ step: Int, _ limit: Int, _ expected: Int) {
        #expect(VolumeSteps.next(current: current, direction: direction, step: step, limit: limit) == expected)
    }
}
