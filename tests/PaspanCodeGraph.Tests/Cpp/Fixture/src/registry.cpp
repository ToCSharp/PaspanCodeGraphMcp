#include "core/registry.h"

namespace core {

int counter = 0;

int max(int a, int b, int c)
{
    return max(max(a, b), c);
}

namespace {

int helper(int x)
{
    return x * 2;
}

} // namespace

static int twice(int x)
{
    return helper(x) + helper(x);
}

CORE_API int total(const Array<int, 4>& values)
{
    int sum = 0;
    Array<int, 4> copy = values;
    for (int value : copy)
    {
        sum += twice(value);
    }

    return CORE_MAX(sum, kLimit);
}

} // namespace core
