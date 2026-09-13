import unittest
from comms_takeover_evidence import analyze_takeover


class TakeoverEvidenceTests(unittest.TestCase):
    def sample(self, phase, **values):
        return dict(ev='commsTakeover', phase=phase, lie='1', glyph='1', cx='0', **values)

    def test_rendered_sequence_passes(self):
        events = [self.sample(p) for p in ('Truth', 'Erase', 'Blank', 'LieHold')]
        result = analyze_takeover(events)
        self.assertEqual(['OK', 'OK'], [level for level, _ in result])

    def test_delivered_but_not_rendered_fails(self):
        for field, value in [('lie', '0'), ('glyph', '0'), ('cx', '3')]:
            event = self.sample('LieHold')
            event[field] = value
            self.assertEqual('FAIL', analyze_takeover([event])[0][0])

    def test_absent_instrument_is_not_success(self):
        self.assertEqual([], analyze_takeover([{'ev': 'comms', 'id': 'Takeover'}]))

    def test_partial_capture_is_inconclusive(self):
        self.assertEqual('WARN', analyze_takeover([self.sample('Truth')])[0][0])


if __name__ == '__main__':
    unittest.main()
