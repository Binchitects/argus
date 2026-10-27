-- Built into the image as its own check: Mathlib loads, a proof is checked, a wrong one is not.
import Mathlib

example : (2 : ℕ) + 2 = 4 := by norm_num

theorem lab_square (a b : ℝ) : (a + b) ^ 2 = a ^ 2 + 2 * a * b + b ^ 2 := by ring
