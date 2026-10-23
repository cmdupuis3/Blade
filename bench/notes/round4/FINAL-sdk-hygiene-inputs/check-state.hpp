// Intentionally no include guard: check after EACH runtime include.
#if defined(BLADE_MACRO_PREDEFINED)
#if !defined(far) || !defined(near) || !defined(pascal)
#error caller macro was lost
#endif
#if far != 901 || near != 902 || pascal != 903
#error caller macro replacement was changed
#endif
#else
#if defined(far) || defined(near) || defined(pascal)
#error SDK legacy macro leaked from a Blade runtime include
#endif
#endif
