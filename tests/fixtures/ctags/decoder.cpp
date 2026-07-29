#include "decoder.h"
static int HelperOnly(int x) { return x + 1; }
int PublicImpl(int x) { return HelperOnly(x); }
