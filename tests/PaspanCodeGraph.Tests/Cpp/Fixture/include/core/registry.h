#pragma once

#define CORE_API
#define CORE_MAX(a, b) ((a) > (b) ? (a) : (b))

namespace core {

typedef unsigned long size_type;
using Callback = void (*)(int);

template <typename T>
class Box {
public:
    explicit Box(T value) : value_(value) {}

    T& get() { return value_; }
    const T& get() const { return value_; }

    template <typename U>
    Box<U> map(U (*f)(const T&)) const;

private:
    T value_;
};

template <typename T>
template <typename U>
Box<U> Box<T>::map(U (*f)(const T&)) const
{
    return Box<U>(f(value_));
}

template <typename T, int N>
class Array {
public:
    T& operator[](int i) { return items_[i]; }
    int size() const { return N; }
    T* begin() { return items_; }
    T* end() { return items_ + N; }

private:
    T items_[N];
};

template <typename T>
T max(T a, T b)
{
    return a < b ? b : a;
}

int max(int a, int b, int c);

enum class Color { Red, Green = 2, Blue };
enum Level { Low, High };

constexpr int kLimit = 10;
extern int counter;

CORE_API int total(const Array<int, 4>& values);

} // namespace core
