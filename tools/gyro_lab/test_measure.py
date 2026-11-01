import math
import unittest
from measure import raw_delta, fusion_rate, multiply


class MeasurementTests(unittest.TestCase):
    def test_physical_motion_is_not_injection(self):
        for kind in (4, 16):
            control, target = [], []
            for i in range(80):
                movement = math.sin(i / 7) * 3
                control.append((1_000_000_000 + i * 20_000_000, kind, movement, .2, -.4, 0, 0, 0))
                target.append((1_000_000_000 + i * 20_000_000, kind, movement + .75, .2, -.4, 0, 0, 0))
            delta, count = raw_delta(target, control, kind)
            self.assertEqual(count, 80)
            self.assertAlmostEqual(delta[0], .75)
            self.assertEqual(delta[1:], [0, 0])

    def test_unmatched_or_missing_events_cannot_pass(self):
        records = [(i * 20_000_000, 4, 0, 0, 0, 0, 0, 0) for i in range(40)]
        shifted = [(row[0] + 1, *row[1:]) for row in records]
        with self.assertRaises(ValueError):
            raw_delta(records, shifted, 4)
        with self.assertRaises(ValueError):
            raw_delta(records[:9], records, 4)

    def test_rotation_vectors_remove_physical_orientation(self):
        for kind in (11, 15):
            control, target = [], []
            for i in range(80):
                t = i * .02
                physical = (0, math.sin(t * .4), 0, math.cos(t * .4))
                injected = (math.sin(t * .75 / 2), 0, 0, math.cos(t * .75 / 2))
                control.append((1_000_000_000 + i * 20_000_000, kind, *physical, 0, 0))
                target.append((1_000_000_000 + i * 20_000_000, kind, *multiply(physical, injected), 0, 0))
            rate, count = fusion_rate(target, control, kind)
            self.assertEqual(count, 80)
            self.assertAlmostEqual(rate[0], .75, places=6)
            self.assertAlmostEqual(rate[1], 0, places=6)
            self.assertAlmostEqual(rate[2], 0, places=6)


if __name__ == "__main__":
    unittest.main()
