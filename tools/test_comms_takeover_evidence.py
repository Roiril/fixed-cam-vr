import unittest
from comms_takeover_evidence import analyze_takeover


class TakeoverEvidenceTests(unittest.TestCase):
    def sample(self, phase):
        captured = phase in ('Seized', 'Complete')
        return dict(ev='commsTakeover', phase=phase, reveal='.86',
                    glyph='0' if captured else '1', cut='1' if captured else '0',
                    collapse='1', faceInk='0', started='1')

    def test_applied_capture_and_empty_display_pass(self):
        events = [self.sample(p) for p in ('Output', 'Pursuit', 'Seized', 'Complete')]
        result = analyze_takeover(events)
        self.assertEqual(['OK', 'OK'], [level for level, _ in result])

    def test_finished_visible_or_uncut_output_fails(self):
        for field, value in [('reveal', '1'), ('glyph', '1'), ('cut', '0')]:
            event = self.sample('Seized')
            event[field] = value
            self.assertEqual('FAIL', analyze_takeover([event])[0][0])

    def test_absent_instrument_is_not_success(self):
        self.assertEqual([], analyze_takeover([{'ev': 'comms', 'id': 'Takeover'}]))

    def test_partial_capture_is_inconclusive(self):
        self.assertEqual('WARN', analyze_takeover([self.sample('Output')])[0][0])

    def test_face_resurrection_fails(self):
        event = self.sample('Complete')
        event['faceInk'] = '1'
        self.assertEqual('FAIL', analyze_takeover([event])[0][0])

    def test_same_report_cannot_restart(self):
        events = [self.sample('Seized'), self.sample('Output')]
        self.assertIn('FAIL', [level for level, _ in analyze_takeover(events)])

    def test_next_report_has_an_independent_capture(self):
        events = [self.sample('Seized'), self.sample('Output')]
        events[1]['started'] = '2'
        self.assertNotIn('FAIL', [level for level, _ in analyze_takeover(events)])


if __name__ == '__main__':
    unittest.main()
