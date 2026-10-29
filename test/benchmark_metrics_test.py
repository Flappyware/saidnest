import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "windows"))
from benchmark_whisper import word_error_rate


class BenchmarkMetricTests(unittest.TestCase):
    def test_case_and_punctuation_are_normalized(self):
        self.assertEqual(word_error_rate("Hello, world!", "hello WORLD"), 0)

    def test_insertion_deletion_and_substitution(self):
        self.assertEqual(word_error_rate("one two three", "one four three"), 1 / 3)
        self.assertEqual(word_error_rate("one two three", "one three"), 1 / 3)
        self.assertEqual(word_error_rate("one two three", "one two four three"), 1 / 3)

    def test_silence_has_no_word_error_denominator(self):
        self.assertIsNone(word_error_rate("", "invented text"))

    def test_missing_whole_transcript_is_full_error(self):
        self.assertEqual(word_error_rate("one two three", ""), 1)


if __name__ == "__main__":
    unittest.main()
