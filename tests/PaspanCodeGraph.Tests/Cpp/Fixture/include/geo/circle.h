#pragma once
#include "geo/shape.h"

namespace geo {

class Circle final : public Shape {
public:
    explicit Circle(double radius);
    Circle(const Point& center, double radius);

    double area() const override;
    double perimeter() const override;
    double radius() const { return radius_; }

    static Circle unit();

private:
    double radius_;
};

class Rect : public Shape {
public:
    Rect(double width, double height);

    double area() const override;
    double width() const;
    double height() const;

    /// A corner of the rectangle.
    struct Corner {
        Point at;
        int index;
    };

    Corner corner(int index) const;

private:
    double width_, height_;
};

class Square : public Rect {
public:
    explicit Square(double side) : Rect(side, side) {}

    double area() const override;
};

class Visitor {
public:
    virtual ~Visitor() = default;
    virtual void visit(const Shape& shape) = 0;
};

} // namespace geo
